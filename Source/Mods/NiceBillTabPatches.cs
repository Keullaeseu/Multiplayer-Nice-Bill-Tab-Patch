using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Compat;
using NiceBillTab;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MultiplayerNiceBillTabPatch.Source.Mods;

public partial class NiceBillTabCompat
{
    #region UI invalidation (runs on every client, including inside sync execution)

    // BillStack.AddBill/Delete are synced by Multiplayer core, but NiceBillTab
    // caches the visible queue (filteredBills/filteredRecipes) in statics that
    // otherwise only the acting client invalidates. Without this, remotes keep
    // showing ghost bills until the tab is reopened - and interacting with a
    // deleted bill can desync.
    [MpCompatPostfix(typeof(BillStack), "AddBill")]
    private static void Post_AddBill()
    {
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    [MpCompatPostfix(typeof(BillStack), "Delete")]
    private static void Post_DeleteBill(Bill bill)
    {
        TabBillsDrawer.shouldRefreshFilter = true;
        if (bill == null) return;
        TabBillsDrawer.Selections.RemoveAll(selection => selection?.SelectedBill == bill);
        if (TabBillsDrawer.LockedSelection?.SelectedBill == bill)
            TabBillsDrawer.LockedSelection = null;
    }

    #endregion

    #region Prefixes (UI thread on the acting client -> synced workers)

    // Double-click / recipe click "add bill". UI follow-up stays local.
    [MpCompatPrefix(typeof(TabBillsDrawer), "TryAddBillToQueue")]
    private static bool Prefix_TryAddBill(RecipeSelection selection, ref bool __result)
    {
        __result = false;
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        if (selection?.workTable == null || !selection.workTable.Spawned) return false;
        var recipe = selection.SelectedRecipe;
        if (recipe == null) return false;

        ShowBillWarnings(recipe);
        if (TabBillsDrawer.LockedSelection == selection)
            TabBillsDrawer.LockedSelection = null;

        // Style travels as (hasStyle, styleIndex) and is resolved inside the sync.
        var style = selection.style;
        var hasStyle = style != null;
        SyncedAddBill(selection.workTable, recipe, selection.material, hasStyle, IndexOfStyle(recipe, style), -1);

        if (recipe.conceptLearned != null)
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(recipe.conceptLearned, KnowledgeAmount.Total);
        if (TutorSystem.TutorialMode)
            TutorSystem.Notify_Event((EventPack)("AddBill-" + recipe.LabelCap.Resolve()));

        if (selection.workTable != TabBillsDrawer.LastSelTable)
        {
            CameraJumper.TryJumpAndSelect((GlobalTargetInfo)(Thing)selection.workTable);
            TabBillsDrawer.DoAutomaticScrollDecision(true);
        }
        else
        {
            TabBillsDrawer.SelectBill(selection.workTable.billStack.Bills.LastOrDefault());
            TabBillsDrawer.DoAutomaticScrollDecision();
        }

        __result = true;
        return false;
    }

    // "Create task on available workbench" from the ingredient menu.
    [MpCompatPrefix(typeof(Utils), "TryAddBillToQueue")]
    private static bool Prefix_UtilsTryAddBill(ThingWithComps workbench, RecipeDef recipe, int count, Map map)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        if (workbench == null || recipe == null) return false;

        // Unlike TryAddBillToQueue above, here the warnings block adding (else-branch).
        var blocked = false;
        if (map != null)
        {
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe
                                         && !map.mapPawns.FreeColonists.Any(pawn =>
                                             MechanitorUtility.IsMechanitor(pawn)))
            {
                Find.WindowStack.Add(new Dialog_MessageBox("RecipeRequiresMechanitor".Translate(recipe.LabelCap)));
                blocked = true;
            }
            else if (!map.mapPawns.FreeColonists.Any(colonist => recipe.PawnSatisfiesSkillRequirements(colonist)))
            {
                Bill.CreateNoPawnsWithSkillDialog(recipe);
                blocked = true;
            }
        }

        if (!blocked)
        {
            if (recipe.conceptLearned != null)
                PlayerKnowledgeDatabase.KnowledgeDemonstrated(recipe.conceptLearned, KnowledgeAmount.Total);
            var table = workbench as Building_WorkTable;
            if (table != null)
                SyncedAddBill(table, recipe, null, false, 0, count);

            if (workbench == TabBillsDrawer.LastSelTable)
            {
                TabBillsDrawer.shouldRefreshFilter = true;
                TabBillsDrawer.DoAutomaticScrollDecision(true);
            }
        }

        return false;
    }

    // NOTE: Harmony binds patch parameters by NAME, so these must exactly match
    // the original method's parameter names (SelTable/bill/index here).
    [MpCompatPrefix(typeof(TabBillsDrawer), "InsertBill")]
    private static bool Prefix_InsertBill(Building_WorkTable SelTable, Bill bill, int index)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        if (SelTable?.billStack == null || bill == null) return false;
        var hasStyle = bill.precept != null;
        SyncedInsertBill(SelTable, bill, index, hasStyle, IndexOfStyle(bill.recipe, bill.precept));
        return false;
    }

    // Drag & drop reorder. Translates the filtered-list drop position into a
    // synced (billIndex, billLoadID, dropIndex) triple; the private GetDropIndex
    // helper is reused via reflection so the hit-testing stays identical.
    [MpCompatPrefix(typeof(TabBillsDrawer), "HandleBillDrop")]
    private static bool Prefix_HandleBillDrop(List<Bill> filteredBills, Vector2 mousePosition, float width)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = TabBillsDrawer.LastSelTable;
        if (table?.billStack == null || filteredBills == null) return false;

        int dragged;
        try
        {
            dragged = (int)AccessTools.Field(typeof(TabBillsDrawer), "draggedBillIndex").GetValue(null);
        }
        catch
        {
            return false;
        }

        if (dragged < 0 || dragged >= filteredBills.Count) return false;

        int dropIndex;
        try
        {
            var getDropIndex = AccessTools.Method(typeof(TabBillsDrawer), "GetDropIndex");
            dropIndex = (int)getDropIndex.Invoke(null, new object[] { filteredBills, mousePosition, width });
        }
        catch
        {
            return false;
        }

        if (dropIndex < 0 || dropIndex == dragged) return false;

        var bill = filteredBills[dragged];
        if (bill == null) return false;
        var stackIndex = table.billStack.Bills.IndexOf(bill);
        if (stackIndex < 0) return false;
        SyncedReorderBill(table, stackIndex, bill.loadID, dropIndex);
        return false;
    }

    [MpCompatPrefix(typeof(TabBillsDrawer), "SuspendBill")]
    private static bool Prefix_SuspendBill(Bill bill, bool flag)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = TableOf(bill);
        if (table == null || bill == null) return false;
        var index = table.billStack.Bills.IndexOf(bill);
        if (index < 0) return false;
        SyncedSuspendBill(table, index, bill.loadID, flag);
        return false;
    }

    [MpCompatPrefix(typeof(TabBillsDrawer), "MinusAction")]
    private static bool Prefix_MinusAction(Bill_Production billProd, RecipeDef recipe)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = TableOf(billProd);
        if (table == null || billProd == null || recipe == null) return false;
        var index = table.billStack.Bills.IndexOf(billProd);
        if (index < 0) return false;
        SyncedAdjustBill(table, index, billProd.loadID, GenUI.CurrentAdjustmentMultiplier(), false);
        return false;
    }

    [MpCompatPrefix(typeof(TabBillsDrawer), "PlusAction")]
    private static bool Prefix_PlusAction(Bill_Production billProd, RecipeDef recipe)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = TableOf(billProd);
        if (table == null || billProd == null || recipe == null) return false;
        var index = table.billStack.Bills.IndexOf(billProd);
        if (index < 0) return false;
        SyncedAdjustBill(table, index, billProd.loadID, GenUI.CurrentAdjustmentMultiplier(), true);
        return false;
    }

    // Material pick for an existing bill (ingredient filter change).
    // Preview-only picks (no bill yet) are pure UI and run unpatched.
    // NOTE: Harmony binds patch parameters by NAME - ingDef must match the original.
    [MpCompatPrefix(typeof(RecipeSelection), "SelectBetterMaterial")]
    private static bool Prefix_SelectBetterMaterial(RecipeSelection __instance, ThingDef ingDef)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        if (__instance?.SelectedBill == null) return true;
        var table = TableOf(__instance.SelectedBill);
        if (table == null) return false;
        var index = table.billStack.Bills.IndexOf(__instance.SelectedBill);
        if (index < 0) return false;
        SyncedSetMaterial(table, index, __instance.SelectedBill.loadID, ingDef);
        __instance.DoCache(ingDef);
        TryCompareSelections();
        return false;
    }

    // Safety net for any other SetMaterialToBill caller. Calls originating
    // from inside a synced worker run unpatched (IsExecutingSyncCommand).
    [MpCompatPrefix(typeof(TabBillsDrawer), "SetMaterialToBill")]
    private static bool Prefix_SetMaterialToBill(RecipeSelection selection, RecipeDef recipe, Bill bill)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = TableOf(bill);
        if (table == null || bill == null) return false;
        var index = table.billStack.Bills.IndexOf(bill);
        if (index < 0) return false;
        SyncedSetMaterial(table, index, bill.loadID, selection?.material);
        return false;
    }

    // The repeat-mode label button opens the VANILLA BillRepeatModeUtility menu,
    // whose option actions assign bill.repeatMode directly (unsynced). In MP we
    // open an equivalent menu (same defs, same labels) whose actions go through
    // a synced setter instead. String-based target: if vanilla ever renames it,
    // only this patch logs an error instead of breaking compilation. Calls from
    // inside a synced worker run the vanilla menu unpatched.
    [MpCompatPrefix("RimWorld.BillRepeatModeUtility", "MakeConfigFloatMenu")]
    private static bool Prefix_RepeatModeMenu(Bill_Production bill)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand) return true;
        var table = bill?.billStack?.billGiver as Building_WorkTable;
        if (table?.billStack == null || bill == null) return true;
        var index = table.billStack.Bills.IndexOf(bill);
        if (index < 0) return true;
        var loadID = bill.loadID;
        var options = new List<FloatMenuOption>();
        foreach (var mode in DefDatabase<BillRepeatModeDef>.AllDefs)
        {
            var repeatMode = mode;
            options.Add(new FloatMenuOption(repeatMode.LabelCap,
                () => SyncedSetRepeatMode(table, index, loadID, repeatMode)));
        }

        Find.WindowStack.Add(new FloatMenu(options));
        return false;
    }

    private static bool warnedBizarre;

    // NOTE: Harmony binds patch parameters by NAME - act must match the original.
    // Only reachable with Better Workbenches installed: the inner paste Action
    // belongs to that mod and cannot be serialized. Warn once instead of
    // silently desyncing; vanilla clipboard paths never go through here.
    [MpCompatPrefix(typeof(Utils), "InsertBillBizarre")]
    private static bool Prefix_InsertBillBizarre(Building_WorkTable workTable, Action act, int index)
    {
        if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand && !warnedBizarre)
        {
            warnedBizarre = true;
            Log.Warning(
                $"{LogPrefix} Better Workbenches paste (InsertBillBizarre) is not synced and may cause desyncs.");
        }

        return true;
    }

    #endregion
}
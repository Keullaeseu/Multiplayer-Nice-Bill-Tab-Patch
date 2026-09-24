using Multiplayer.API;
using Multiplayer.Compat;
using NiceBillTab;
using RimWorld;
using UnityEngine;
using Verse;

namespace MultiplayerNiceBillTabPatch.Source.Mods;

public partial class NiceBillTabCompat
{
    #region Synced workers (game state only, run on every client)

    // Exact copy of TabBillsDrawer.MinusAction, but with the adjustment
    // multiplier captured on the acting client passed in explicitly
    // (remote clients don't hold shift/ctrl).
    private static void MinusActionImpl(Bill_Production billProd, RecipeDef recipe, int multiplier)
    {
        if (billProd.repeatMode == BillRepeatModeDefOf.Forever)
        {
            billProd.repeatMode = BillRepeatModeDefOf.RepeatCount;
            billProd.repeatCount = 1;
        }
        else if (billProd.repeatMode == BillRepeatModeDefOf.TargetCount)
        {
            var adjustment = recipe.targetCountAdjustment * multiplier;
            billProd.targetCount = Mathf.Max(0, billProd.targetCount - adjustment);
            billProd.unpauseWhenYouHave = Mathf.Max(0, billProd.unpauseWhenYouHave - adjustment);
        }
        else if (billProd.repeatMode == BillRepeatModeDefOf.RepeatCount)
        {
            billProd.repeatCount = Mathf.Max(0, billProd.repeatCount - multiplier);
        }
        else if (billProd.repeatMode.defName == "TD_PersonCount" || billProd.repeatMode.defName == "TD_XPerPerson")
        {
            var personCountAdjustment = recipe.targetCountAdjustment * multiplier;
            if (billProd.repeatMode.defName == "TD_XPerPerson")
            {
                billProd.targetCount = Mathf.Max(0, billProd.targetCount - personCountAdjustment);
                billProd.unpauseWhenYouHave = Mathf.Max(0, billProd.unpauseWhenYouHave - personCountAdjustment);
            }
            else
            {
                billProd.targetCount -= personCountAdjustment;
                billProd.unpauseWhenYouHave -= personCountAdjustment;
            }
        }
    }

    // Exact copy of TabBillsDrawer.PlusAction with explicit multiplier.
    private static void PlusActionImpl(Bill_Production billProd, RecipeDef recipe, int multiplier)
    {
        if (billProd.repeatMode == BillRepeatModeDefOf.Forever)
        {
            billProd.repeatMode = BillRepeatModeDefOf.RepeatCount;
            billProd.repeatCount = 1;
        }
        else if (billProd.repeatMode == BillRepeatModeDefOf.TargetCount)
        {
            var adjustment = recipe.targetCountAdjustment * multiplier;
            billProd.targetCount += adjustment;
            billProd.unpauseWhenYouHave += adjustment;
        }
        else if (billProd.repeatMode == BillRepeatModeDefOf.RepeatCount)
        {
            billProd.repeatCount += multiplier;
        }
        else if (billProd.repeatMode.defName == "TD_PersonCount" || billProd.repeatMode.defName == "TD_XPerPerson")
        {
            var personCountAdjustment = recipe.targetCountAdjustment * multiplier;
            billProd.targetCount += personCountAdjustment;
            billProd.unpauseWhenYouHave += personCountAdjustment;
        }
    }

    // Fresh bills are built INSIDE the sync from plain data (all MP-native
    // args), so every client constructs an identical bill with live session
    // references - no Precept objects cross the wire and nothing depends on
    // expose fidelity. repeatForCount < 0 leaves repeat settings at default.
    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedAddBill(Building_WorkTable table, RecipeDef recipe, ThingDef material, bool hasStyle,
        int styleIndex, int repeatForCount)
    {
        if (table?.billStack == null || recipe == null) return;
        var bill = recipe.MakeNewBill(ResolveStyle(recipe, hasStyle, styleIndex));
        table.billStack.AddBill(bill);
        ApplyMaterialToBill(material, recipe, bill);
        // Fresh bills never carry a custom name, so no HasCustomName check needed.
        if (Settings.EnableAutoNaming && bill is Bill_Production labeledBill && material != null)
            labeledBill.RenamableLabel = material.LabelCap;
        if (repeatForCount >= 0 && bill is Bill_Production productionBill && recipe.products != null &&
            recipe.products.Count > 0)
        {
            var productCount = recipe.products[0].count;
            if (productCount < 1) productCount = 1;
            productionBill.repeatMode = BillRepeatModeDefOf.RepeatCount;
            productionBill.repeatCount = Mathf.CeilToInt((float)repeatForCount / productCount);
        }

        TabBillsDrawer.shouldRefreshFilter = true;
    }

    // Clipboard paste: the bill still crosses via expose, but out-of-graph
    // references (notably precept) may not survive it, so the style identity
    // travels alongside and is repaired deterministically after insert.
    // Do NOT call InitializeAfterClone here. index < 0 appends via AddBill.
    [MpCompatSyncMethod(SyncContext.MapSelected, exposeParameters = new[] { 1 })]
    private static void SyncedInsertBill(Building_WorkTable table, Bill bill, int index, bool hasStyle, int styleIndex)
    {
        if (table?.billStack == null || bill == null) return;
        bill.billStack = table.billStack;
        bill.loadID = Find.UniqueIDsManager.GetNextBillID();
        SetBillPrecept(bill, ResolveStyle(bill.recipe, hasStyle, styleIndex));
        if (index < 0)
        {
            table.billStack.AddBill(bill);
        }
        else
        {
            var list = table.billStack.Bills;
            if (index > list.Count) index = list.Count;
            list.Insert(index, bill);
        }

        TabBillsDrawer.shouldRefreshFilter = true;
    }

    // Mirrors HandleBillDrop exactly (remove + insert at the filtered-list
    // drop index, including its quirk when a text filter is active).
    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedReorderBill(Building_WorkTable table, int billIndex, int billLoadID, int dropIndex)
    {
        var bill = FindBill(table, billIndex, billLoadID);
        if (bill == null) return;
        var list = table.billStack.Bills;
        list.Remove(bill);
        if (dropIndex < 0) dropIndex = 0;
        if (dropIndex > list.Count) dropIndex = list.Count;
        list.Insert(dropIndex, bill);
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedSuspendBill(Building_WorkTable table, int billIndex, int billLoadID, bool suspended)
    {
        var bill = FindBill(table, billIndex, billLoadID);
        if (bill == null) return;
        bill.suspended = suspended;
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedAdjustBill(Building_WorkTable table, int billIndex, int billLoadID, int multiplier,
        bool plus)
    {
        if (!(FindBill(table, billIndex, billLoadID) is Bill_Production productionBill)) return;
        if (productionBill.recipe == null) return;
        if (plus) PlusActionImpl(productionBill, productionBill.recipe, multiplier);
        else MinusActionImpl(productionBill, productionBill.recipe, multiplier);
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedSetMaterial(Building_WorkTable table, int billIndex, int billLoadID, ThingDef material)
    {
        var bill = FindBill(table, billIndex, billLoadID);
        if (bill == null) return;
        ApplyMaterialToBill(material, bill.recipe, bill);
        // Keep the preview caches of whoever has this bill selected consistent.
        foreach (var selection in TabBillsDrawer.Selections)
            if (selection?.SelectedBill == bill)
                selection.DoCache(material);
        TryCompareSelections();
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    [MpCompatSyncMethod(SyncContext.MapSelected)]
    private static void SyncedSetRepeatMode(Building_WorkTable table, int billIndex, int billLoadID,
        BillRepeatModeDef mode)
    {
        var bill = FindBill(table, billIndex, billLoadID);
        if (bill is Bill_Production productionBill && mode != null)
            productionBill.repeatMode = mode;
        TabBillsDrawer.shouldRefreshFilter = true;
    }

    #endregion
}
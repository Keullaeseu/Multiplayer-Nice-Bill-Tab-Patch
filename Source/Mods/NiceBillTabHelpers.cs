using HarmonyLib;
using NiceBillTab;
using RimWorld;
using Verse;

namespace MultiplayerNiceBillTabPatch.Source.Mods;

public partial class NiceBillTab
{
    #region Helpers (local-only, never synced)

    private static Building_WorkTable TableOf(Bill bill)
    {
        return bill?.billStack?.billGiver as Building_WorkTable;
    }

    private static Bill FindBill(Building_WorkTable table, int billIndex, int billLoadID)
    {
        var bills = table?.billStack?.Bills;
        if (bills == null) return null;
        if ((uint)billIndex < (uint)bills.Count)
        {
            var fast = bills[billIndex];
            if (fast != null && fast.loadID == billLoadID) return fast;
        }

        foreach (var candidateBill in bills)
            if (candidateBill != null && candidateBill.loadID == billLoadID)
                return candidateBill;
        Log.Warning($"{LogPrefix} Bill not found (index {billIndex}, loadID {billLoadID}) - skipping synced bill op.");
        return null;
    }

    // RecipeComparator visibility is not guaranteed, so refresh it via reflection.
    // It only updates comparison-bar UI caches, failure is non-fatal.
    private static void TryCompareSelections()
    {
        try
        {
            var type = AccessTools.TypeByName("NiceBillTab.RecipeComparator");
            var method = type == null ? null : AccessTools.Method(type, "TryCompareSelections", new[] { typeof(bool) });
            method?.Invoke(null, new object[] { true });
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} selection compare refresh failed: {exception.GetType().Name}");
        }
    }

    // Same warnings the mod shows in single-player. Local-only dialogs.
    private static void ShowBillWarnings(RecipeDef recipe)
    {
        var map = TabBillsDrawer.LastSelTable?.Map;
        if (map == null || recipe == null) return;

        if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe
                                     && !map.mapPawns.FreeColonists.Any(pawn =>
                                         MechanitorUtility.IsMechanitor(pawn)))
            Find.WindowStack.Add(new Dialog_MessageBox("RecipeRequiresMechanitor".Translate(recipe.LabelCap)));
        else if (!map.mapPawns.FreeColonists.Any(colonist => recipe.PawnSatisfiesSkillRequirements(colonist)))
            Bill.CreateNoPawnsWithSkillDialog(recipe);
    }

    private static void ApplyMaterialToBill(ThingDef material, RecipeDef recipe, Bill bill)
    {
        if (material == null || bill?.ingredientFilter == null) return;
        var produced = recipe?.ProducedThingDef;
        if (produced == null || !produced.MadeFromStuff) return;
        bill.ingredientFilter.SetDisallowAll();
        bill.ingredientFilter.SetAllow(material, true);
    }

    // Ideology style identity without sending Precept objects over the wire
    // (MP has no worker for them and Scribe references don't survive expose).
    // Both sides enumerate the same source GetAvailableRecipes uses, so the
    // (hasStyle, styleIndex) pair resolves deterministically everywhere.
    private static int IndexOfStyle(RecipeDef recipe, Precept_ThingStyle style)
    {
        if (recipe?.ProducedThingDef == null || style == null) return -1;
        var allIdeos = Faction.OfPlayer?.ideos?.AllIdeos;
        if (allIdeos == null) return -1;
        var index = 0;
        foreach (var ideo in allIdeos)
        foreach (var possibleBuilding in ideo.cachedPossibleBuildings)
            if (possibleBuilding != null && possibleBuilding.ThingDef == recipe.ProducedThingDef)
            {
                if (ReferenceEquals(possibleBuilding, style)) return index;
                index++;
            }

        return -1;
    }

    private static Precept_ThingStyle ResolveStyle(RecipeDef recipe, bool hasStyle, int styleIndex)
    {
        if (!hasStyle || recipe?.ProducedThingDef == null) return null;
        var allIdeos = Faction.OfPlayer?.ideos?.AllIdeos;
        if (allIdeos == null) return null;
        var matches = new List<Precept_ThingStyle>();
        foreach (var ideo in allIdeos)
        foreach (var possibleBuilding in ideo.cachedPossibleBuildings)
            if (possibleBuilding != null && possibleBuilding.ThingDef == recipe.ProducedThingDef)
                matches.Add(possibleBuilding);
        if (matches.Count == 0) return null;
        if ((uint)styleIndex >= (uint)matches.Count) return matches[0];
        return matches[styleIndex];
    }

    // Bill.precept shape (field vs property) is not guaranteed; reflection
    // covers both. Worst case the style stays as-deserialized (today's behavior).
    private static void SetBillPrecept(Bill bill, Precept_ThingStyle style)
    {
        try
        {
            var preceptField = AccessTools.Field(typeof(Bill), "precept");
            if (preceptField != null)
            {
                preceptField.SetValue(bill, style);
                return;
            }

            var preceptProperty = AccessTools.Property(typeof(Bill), "precept");
            preceptProperty?.GetSetMethod(true)?.Invoke(bill, new object[] { style });
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} Could not restore bill style: {exception.GetType().Name}");
        }
    }

    #endregion
}
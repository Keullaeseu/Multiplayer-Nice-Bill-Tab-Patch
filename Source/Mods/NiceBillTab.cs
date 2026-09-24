using Multiplayer.Compat;
using Verse;

namespace MultiplayerNiceBillTabPatch.Source.Mods;

/// <summary>
///     Multiplayer patch for Nice Bill Tab by Andromeda, Last Update: 17 Jun @ 7:16pm 2026
///     https://steamcommunity.com/sharedfiles/filedetails/?id=3520130671
///     Follows the rwmt/Multiplayer-Compatibility pattern: UI event handlers are
///     Harmony-prefixed and redirected into [MpCompatSyncMethod] workers that only
///     touch game state, so every client executes the same mutation.
///     What is synced:
///     - adding bills (double-click / "add bill" / "craft on available workbench")
///     - pasting clipboard bills at a specific queue position
///     - drag &amp; drop reordering of the bill queue
///     - suspend / unsuspend from the queue overlay and classic suspend button
///     - repeat count / target count +/- buttons (with the shift/ctrl multiplier captured on the acting client)
///     - material selection for existing bills (ingredient filter changes)
///     - repeat-mode dropdown (equivalent menu routed through a synced setter)
///     Relied upon from Multiplayer core (not patched here):
///     - BillStack.AddBill / BillStack.Delete (used by add, workbench paste button and the delete button)
///     - Dialog_BillConfig (opened via the "Details..." button)
///     Known limitations (documented, not silently broken):
///     - the store-mode toggle in DoExtraQueuedBillButtons calls vanilla
///     Bill_Production.SetStoreMode directly; left untouched on purpose so the
///     vanilla dialog path keeps working.
///     - Better Workbenches (falconne.bwm) copy/paste/link actions go through that
///     mod's own clipboard and are only warned about (see prefix below).
///     Pure UI state (Selections, LockedSelection, filter text, scroll positions,
///     stat caches) is intentionally never synced.
/// </summary>
[MpCompatFor("Andromeda.NiceBillTab")]
public partial class NiceBillTabCompat
{
    internal const string LogPrefix = "[Multiplayer Nice Bill Tab Patch]";

    public NiceBillTabCompat(ModContentPack mod)
    {
        LongEventHandler.ExecuteWhenFinished(LatePatch);
    }

    private void LatePatch()
    {
        Log.Message($"{LogPrefix} Initializing...");

        MpCompatPatchLoader.LoadPatch(this);

        Log.Message($"{LogPrefix} Initialized.");
    }
}
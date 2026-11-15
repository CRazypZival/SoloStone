using HarmonyLib;
using Hearthstone.DataModels;
using UnityEngine;

internal static partial class NativeArena
{
    private static float nextPresentation;
    private static void UpdatePresentation()
    {
        if (!InDraft || Time.realtimeSinceStartup < nextPresentation) return;
        nextPresentation = Time.realtimeSinceStartup + .5f;
        var landing = ArenaLandingPageManager.Get();
        var page = landing?.GetArenaLandingPage();
        if (page == null) return;
        var view = Read();
        var root = AccessTools.Field(typeof(ArenaLandingPage), "m_playButtonNoActiveRun").GetValue(page) as GameObject;
        var button = root?.GetComponentInChildren<PlayButton>(true);
        if (button != null)
        {
            button.SetText("Enter Arena");
            button.SetSecondaryText(view.EntryCost + " gold");
            button.SetTertiaryText(view.GoldBalance + " gold available");
        }
        // Never reshape the native hierarchy while the scene is still loading: its widgets are settling their first states, and
        // SceneMgr waits for that (DraftDisplay.NotifySceneLoadedWhenReady). The box is closed until then, so nothing shows early.
        if (SceneMgr.Get().IsSceneLoaded() && !SceneMgr.Get().IsTransitioning())
        {
            if (AccessTools.Field(typeof(ArenaLandingPageManager), "m_tavernTicketDisplay").GetValue(landing) is TavernTicketDisplay tickets)
                tickets.gameObject.SetActive(false);
            if (AccessTools.Field(typeof(ArenaLandingPage), "m_getMoreTicketsButtonWidget").GetValue(page) is Component moreTickets)
                moreTickets.gameObject.SetActive(false);
        }
        if (AccessTools.Field(typeof(ArenaLandingPageManager), "m_arenaLandingPageDataModel").GetValue(landing) is ArenaLandingPageDataModel model)
        {
            model.isUndergroundUnlocked = true; // Presentation only: its click always explains "coming later".

        }
        NoRating(landing);
    }
    // Set the landing models as Awake builds them, before any widget binds: no data change while the first states settle.
    private static void LandingModels(ArenaLandingPageManager __instance)
    {
        if (!Offline || __instance == null) return;
        if (AccessTools.Field(typeof(ArenaLandingPageManager), "m_arenaLandingPageDataModel").GetValue(__instance) is ArenaLandingPageDataModel model)
            model.isUndergroundUnlocked = true; // Presentation only: its click always explains "coming later".
        NoRating(__instance);
    }
    private static bool NoRating(ArenaLandingPageManager __instance)
    {
        if (!Offline) return true;
        if (AccessTools.Field(typeof(ArenaLandingPageManager), "m_arenaLandingPageDataModel").GetValue(__instance) is ArenaLandingPageDataModel model)
            model.rating = model.undergroundRating = string.Empty;
        foreach (string field in new[] { "m_arenaNormalLandingPageDataModel", "m_arenaUndergroundLandingPageDataModel" })
            if (AccessTools.Field(typeof(ArenaLandingPageManager), field).GetValue(__instance) is ArenaLandingPageModeDataModel mode)
                mode.rating = string.Empty;
        return false;
    }
}

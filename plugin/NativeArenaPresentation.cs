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
        // Ticket UI has no offline meaning. Hide only an object of its own: never one that holds the Arena screen itself.
        // Never during loading either: SceneMgr waits for the first states to settle (DraftDisplay.NotifySceneLoadedWhenReady).
        if (SceneMgr.Get().IsSceneLoaded() && !SceneMgr.Get().IsTransitioning())
        {
            var tickets = AccessTools.Field(typeof(ArenaLandingPageManager), "m_tavernTicketDisplay").GetValue(landing) as TavernTicketDisplay;
            HideOwnPart(tickets, "tickets", landing, page);
            HideOwnPart(AccessTools.Field(typeof(ArenaLandingPage), "m_getMoreTicketsButtonWidget").GetValue(page) as Component, "moreTickets", landing, page);
        }
        if (AccessTools.Field(typeof(ArenaLandingPageManager), "m_arenaLandingPageDataModel").GetValue(landing) is ArenaLandingPageDataModel model)
        {
            model.isUndergroundUnlocked = true; // Presentation only: its click always explains "coming later".
        }
        NoRating(landing);
    }
    private static readonly System.Collections.Generic.HashSet<string> partsLogged = new System.Collections.Generic.HashSet<string>();
    private static void HideOwnPart(Component part, string name, ArenaLandingPageManager landing, ArenaLandingPage page)
    {
        if (part == null || !part.gameObject.activeSelf) return;
        var target = part.gameObject.transform;
        bool Holds(Component screen) => screen != null && (screen.transform == target || screen.transform.IsChildOf(target));
        // SetActive(false) on an ancestor of DraftDisplay stops its coroutines (the DRAFT load never finishes) and blanks the screen.
        bool shared = Holds(landing) || Holds(page) || Holds(DraftDisplay.Get());
        if (partsLogged.Add(name))
            log.LogInfo("NATIVE_ARENA_PRESENTATION " + name + " path=" + PathOf(target, null, 12) + " children=" + target.childCount
                + (shared ? " holds the Arena screen: left active" : " hidden"));
        if (!shared) part.gameObject.SetActive(false);
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hearthstone.UI;
using UnityEngine;

// DRAFT boot watch. SceneMgr finishes DRAFT only when a coroutine calls NotifySceneLoaded: DraftDisplay.NotifySceneLoadedWhenReady
// (confirm button, hero power actor, a draft mode, NetCache forge batch, achievements, the landing widget done changing states)
// or DraftScene.NotifySceneLoadedWhenReady (landing manager loaded, then DraftDisplay loaded). Every one of those waits is read
// here, the coroutines' own progress is recorded, and their exceptions are logged, so a stuck load names its condition. A load
// that never finishes leaves to HUB through the client's own navigation instead of keeping the box closed for good.
internal static partial class NativeArena
{
    private const float WaitReportSeconds = 10f, StuckSeconds = 30f;
    private static float draftLoadBegan = -1, nextWaitRead;
    private static bool waitsReported;
    private static readonly List<KeyValuePair<string, string>> waits = new List<KeyValuePair<string, string>>();
    private static string firstWait;
    // The iterator's state is the yield it is parked at: Roslyn numbers the yields 1..n in source order; -1 = finished.
    private sealed class Routine { internal int State, Steps; internal bool Finished; internal string Error; }
    private static readonly Dictionary<string, Routine> routines = new Dictionary<string, Routine>();
    // Keyed by the iterator class (one per coroutine), not by MethodBase identity.
    private static readonly Dictionary<string, string> routineNames = new Dictionary<string, string>();
    private static readonly Dictionary<Type, FieldInfo> stateFields = new Dictionary<Type, FieldInfo>();
    // Wait names per yield, from the pinned client's source order.
    private static readonly Dictionary<string, string[]> routineWaits = new Dictionary<string, string[]> {
        { "display", new[] { "confirmButton", "heroPower", "mode", "netCacheReady", "achievements", "landingWidget", "landingWidgetChangingStates" } },
        { "scene", new[] { "landingManager", "landingLoadingComplete", "draftDisplay", "draftDisplayLoadingComplete" } },
        { "landing", new[] { "normalWidgetLoaded", "undergroundWidgetLoaded", "doneInitialStateChanging" } } };
    private static readonly string[] forgeCaches = { "NetCacheProfileNotices", "NetCacheDecks", "NetCacheCollection",
        "NetCacheArcaneDustBalance", "NetCacheClientOptions", "NetCacheFeatures", "NetCacheHeroLevels" };

    // Diagnostics only: a failed install is logged and never disables Arena (the stuck-load exit needs no patch).
    private static void InstallLoadWatch(Harmony harmony)
    {
        try { InstallLoadPatches(harmony); }
        catch (Exception error) { log?.LogWarning("NATIVE_ARENA_WAIT boot watch unavailable: " + error.GetBaseException().Message); }
    }
    private static void InstallLoadPatches(Harmony harmony)
    {
        foreach (var (owner, name, label) in new[] {
            (typeof(DraftDisplay), "NotifySceneLoadedWhenReady", "display"), (typeof(DraftScene), "NotifySceneLoadedWhenReady", "scene"),
            (typeof(ArenaLandingPageManager), "NotifySceneLoadedWhenReady", "landing"), (typeof(DraftDisplay), "InitializeDraftScreen", "initialize") })
        {
            var moveNext = AccessTools.EnumeratorMoveNext(Required(owner, name)) ?? throw new MissingMethodException(owner.FullName, name + " MoveNext");
            routineNames[RoutineKey(moveNext)] = label;
            harmony.Patch(moveNext, postfix: new HarmonyMethod(typeof(NativeArena), nameof(RoutineStep)),
                finalizer: new HarmonyMethod(typeof(NativeArena), nameof(RoutineFault)));
        }
        // A throw before StartCoroutine means the wait never starts: Unity logs it, this names it beside the Arena lines.
        foreach (var (owner, name) in new[] { (typeof(DraftDisplay), "Awake"), (typeof(DraftDisplay), "Start"),
            (typeof(ArenaLandingPageManager), "Awake"), (typeof(ArenaLandingPageManager), "Start"), (typeof(DraftScene), "OnUIScreenLoaded") })
            harmony.Patch(Required(owner, name), finalizer: new HarmonyMethod(typeof(NativeArena), nameof(BootFault)));
    }

    private static string RoutineKey(MethodBase method) => method?.DeclaringType?.FullName ?? "";
    private static Routine RoutineFor(MethodBase method)
    {
        if (method == null || !routineNames.TryGetValue(RoutineKey(method), out string name)) return null;
        if (!routines.TryGetValue(name, out Routine routine)) routines[name] = routine = new Routine();
        return routine;
    }
    private static void RoutineStep(object __instance, bool __result, MethodBase __originalMethod)
    {
        if (!NativeUIProbe.Active || __instance == null) return;
        var routine = RoutineFor(__originalMethod);
        if (routine == null) return;
        var type = __instance.GetType();
        if (!stateFields.TryGetValue(type, out FieldInfo field)) stateFields[type] = field = AccessTools.Field(type, "<>1__state");
        routine.Steps++;
        routine.Finished = !__result;
        if (field != null) routine.State = (int)field.GetValue(__instance);
    }
    private static Exception RoutineFault(Exception __exception, MethodBase __originalMethod)
    {
        if (__exception != null && NativeUIProbe.Active)
        {
            var routine = RoutineFor(__originalMethod);
            if (routine != null) { routine.Error = __exception.GetBaseException().Message; routine.Finished = true; }
            log?.LogError("NATIVE_ARENA_BOOT_ERROR " + (routineNames.TryGetValue(RoutineKey(__originalMethod), out string name) ? name : __originalMethod?.Name)
                + " coroutine threw: " + __exception.GetBaseException());
        }
        return __exception;
    }
    private static Exception BootFault(Exception __exception, MethodBase __originalMethod)
    {
        if (__exception != null && NativeUIProbe.Active)
        {
            if (!routines.TryGetValue("boot", out Routine boot)) routines["boot"] = boot = new Routine();
            boot.Error = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name + ": " + __exception.GetBaseException().Message;
            log?.LogError("NATIVE_ARENA_BOOT_ERROR " + __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name + " threw: " + __exception.GetBaseException());
        }
        return __exception;
    }

    // Called every frame by NativeUIProbe.Update. Never throws: a diagnostic must not block the native UI.
    internal static void WatchSceneLoad()
    {
        try
        {
            // NativeUIProbe.Active, not Offline: a load stuck after Arena disabled itself must still be left.
            bool loading = NativeUIProbe.Active && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.DRAFT && SceneMgr.Get().IsTransitioning();
            float now = Time.realtimeSinceStartup;
            if (!loading)
            {
                if (draftLoadBegan >= 0)
                {
                    bool loaded = SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.DRAFT;
                    log.LogInfo("NATIVE_ARENA_LOAD " + (loaded ? "finished" : "left") + " t=" + (now - draftLoadBegan).ToString("0.0") + " " + RoutineSummary());
                    draftLoadBegan = -1;
                }
                return;
            }
            if (draftLoadBegan < 0)
            {
                draftLoadBegan = now; nextWaitRead = now + 1; waitsReported = false;
                routines.Clear(); waits.Clear(); firstWait = null;
            }
            float elapsed = now - draftLoadBegan;
            if (now >= nextWaitRead) { nextWaitRead = now + 1; ReadWaits(); }
            if (!waitsReported && elapsed >= WaitReportSeconds) { waitsReported = true; ReadWaits(); ReportWaits(elapsed); }
            if (elapsed >= StuckSeconds && loadFailure == null) LeaveStuckDraft(elapsed);
        }
        catch (Exception error)
        {
            nextWaitRead = Time.realtimeSinceStartup + 5;
            log?.LogWarning("NATIVE_ARENA_WAIT read failed: " + error.GetBaseException().Message);
        }
    }

    private static void LeaveStuckDraft(float elapsed)
    {
        // Block first: whatever happens next, entry is refused for this session instead of hanging again.
        loadFailure = "DRAFT did not finish loading in " + elapsed.ToString("0") + "s, waiting on " + (firstWait ?? "unknown");
        try { ReadWaits(); ReportWaits(elapsed); } catch (Exception) { }
        string refused = NativeUIProbe.LeaveStuckScene(SceneMgr.Mode.DRAFT, SceneMgr.Mode.HUB);
        log.LogError("NATIVE_ARENA_STUCK " + loadFailure + (refused == null ? "; returning to HUB" : "; cannot return to HUB: " + refused));
        Unavailable();
    }

    private static void ReportWaits(float elapsed)
    {
        string t = "t=" + elapsed.ToString("0.0") + " ";
        foreach (var wait in waits) log.LogWarning("NATIVE_ARENA_WAIT " + t + wait.Key + "=" + wait.Value);
        log.LogWarning("NATIVE_ARENA_WAIT " + t + "first=" + (firstWait ?? "none") + " " + RoutineSummary());
    }

    private static string RoutineSummary() => string.Join(" ", new[] { "display", "scene", "landing", "initialize", "boot" }
        .Select(name => "routine." + name + "=" + RoutineText(name)));
    private static string RoutineText(string name)
    {
        if (!routines.TryGetValue(name, out Routine routine)) return "not-run";
        if (routine.Error != null) return "threw(" + Bound(routine.Error, 160) + ")";
        if (routine.Finished) return "finished";
        string wait = routineWaits.TryGetValue(name, out string[] names) && routine.State >= 1 && routine.State <= names.Length ? ":" + names[routine.State - 1] : "";
        return "parked@" + routine.State + wait + "/steps=" + routine.Steps;
    }

    private static void ReadWaits()
    {
        waits.Clear(); firstWait = null;
        void Add(string name, object value, bool blocking = false)
        {
            waits.Add(new KeyValuePair<string, string>(name, value is bool flag ? (flag ? "true" : "false") : Bound(value?.ToString() ?? "null", 600)));
            if (blocking && firstWait == null) firstWait = name;
        }
        var scene = SceneMgr.Get();
        Add("scene.mode", scene.GetMode()); Add("scene.next", scene.GetNextMode());
        Add("scene.loaded", scene.IsSceneLoaded()); Add("scene.transitioning", scene.IsTransitioning());
        object pegasus = AccessTools.Field(typeof(SceneMgr), "m_scene")?.GetValue(scene);
        Add("scene.object", Alive(pegasus) ? pegasus.GetType().Name : "null", !(Alive(pegasus) && pegasus is DraftScene));
        if (pegasus is DraftScene draftScene && Alive(draftScene)) Add("scene.landingPrefab", Alive(Field(draftScene, "m_arenaLandingPageWidget")));
        Add("routine.scene", RoutineText("scene"));

        // DraftDisplay.NotifySceneLoadedWhenReady, in its own order: the first false one is what SceneMgr is waiting for.
        var display = DraftDisplay.Get();
        Add("display.exists", Alive(display), !Alive(display));
        if (Alive(display))
        {
            bool confirm = Alive(Field(display, "m_confirmButton")), hero = Alive(Field(display, "m_heroPower"));
            Add("display.confirmButton", confirm, !confirm);
            Add("display.heroPower", hero, !hero);
            object mode = Field(display, "m_currentMode");
            Add("display.mode", mode, mode?.ToString() == "INVALID");
            bool net = Flag(display, "m_netCacheReady");
            Add("display.netCacheReady", net, !net);
            if (!net) Add("netcache.missing", MissingForgeCaches());
            // The coroutine's IsReady() call is replaced offline (NativeArenaRetail): only a missing service could still throw.
            Add("achievements.service", AchieveManager.Get() != null);
            var landingWidget = Field(display, "m_arenaLandingPageManagerWidget") as Widget;
            Add("display.landingWidget", Alive(landingWidget), !Alive(landingWidget));
            if (Alive(landingWidget))
            {
                bool changing = landingWidget.IsChangingStates;
                Add("display.landingWidgetChangingStates", changing, changing);
                if (changing) Add("widgets.changing", ChangingStates(landingWidget));
            }
            Add("display.loadingComplete", display.IsLoadingComplete());
        }
        Add("routine.display", RoutineText("display"));
        Add("routine.initialize", RoutineText("initialize"));

        // ArenaLandingPageManager.NotifySceneLoadedWhenReady gates DraftScene's own NotifySceneLoaded (DraftDisplay's comes first).
        var landing = ArenaLandingPageManager.Get();
        Add("landing.exists", Alive(landing));
        if (Alive(landing))
        {
            Add("landing.normalWidgetLoaded", Flag(landing, "m_isNormalWidgetLoaded"));
            Add("landing.undergroundWidgetLoaded", Flag(landing, "m_isUndergroundWidgetLoaded"));
            Add("landing.doneInitialStateChanging", Flag(landing, "m_isWidgetDoneInitialStateChanging"));
            Add("landing.loadingComplete", landing.IsLoadingComplete());
        }
        Add("routine.landing", RoutineText("landing"));
        Add("routine.boot", RoutineText("boot"));
        Add("draft.session", DraftManager.Get() == null ? "missing" : DraftManager.Get().GetArenaState(false) + " wins=" + DraftManager.Get().GetWins()
            + " losses=" + DraftManager.Get().GetLosses() + " client=" + DraftManager.Get().GetCurrentClientState());
        Add("replies.pending", string.Join(",", replies.Select(reply => reply.Id.ToString()).ToArray()));
        Add("replies.last", string.Join(",", lastReplies.Select(id => id.ToString()).ToArray()));
        // Every wait met but no NotifySceneLoaded: the coroutine is not running (it threw, or Start never reached it).
        if (firstWait == null && Alive(display) && !display.IsLoadingComplete()) firstWait = "routine.display";
    }

    private static string MissingForgeCaches()
    {
        var cache = AccessTools.Field(typeof(NetCache), "m_netCache")?.GetValue(NetCache.Get()) as IDictionary;
        if (cache == null) return "unreadable";
        var missing = new List<string>();
        foreach (string name in forgeCaches)
        {
            object value = null;
            foreach (DictionaryEntry entry in cache) if (((Type)entry.Key).Name == name) value = entry.Value;
            if (value == null) missing.Add(name);
        }
        var features = NetCache.Get().GetNetObject<NetCache.NetCacheFeatures>();
        if (features != null && !features.Games.Forge) missing.Add("Features.Games.Forge=false");
        return missing.Count == 0 ? "none (callback not delivered)" : string.Join(",", missing.ToArray());
    }

    // The active leaves still changing state under the landing widget: visual controllers mid-transition and widgets not done.
    private static string ChangingStates(Widget root)
    {
        var rows = new List<string>();
        foreach (var controller in root.GetComponentsInChildren<VisualController>(true))
            if (controller != null && controller.isActiveAndEnabled && controller.IsChangingStates && rows.Count < 8)
                rows.Add(PathOf(controller.transform, root.transform) + "[" + controller.Label + ":" + controller.State + "->" + controller.RequestedState
                    + (controller.HasPendingActions ? ",pending" : "") + "]");
        foreach (var widget in root.GetComponentsInChildren<Widget>(true))
            if (widget != null && widget != root && widget.IsActive && widget.InitState != Widget.InitializationState.Done && rows.Count < 14)
                rows.Add(PathOf(widget.transform, root.transform) + "[" + widget.GetType().Name + ":" + widget.InitState + "]");
        return rows.Count == 0 ? "none found" : string.Join(" | ", rows.ToArray());
    }
    private static string PathOf(Transform node, Transform root)
    {
        var parts = new List<string>();
        for (var at = node; at != null && at != root && parts.Count < 4; at = at.parent) parts.Insert(0, at.name);
        return string.Join("/", parts.ToArray());
    }

    private static bool Alive(object value) => value is UnityEngine.Object unity ? unity != null : value != null;
    private static bool Flag(object owner, string name) => owner != null && AccessTools.Field(owner.GetType(), name)?.GetValue(owner) is bool value && value;
    private static object Field(object owner, string name) => owner == null ? null : AccessTools.Field(owner.GetType(), name)?.GetValue(owner);
    private static string Bound(string value, int max) => value == null || value.Length <= max ? value : value.Substring(0, max) + "...";
}

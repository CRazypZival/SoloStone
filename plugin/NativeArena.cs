using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using PegasusShared;
using PegasusUtil;
using UnityEngine;

// Transport only. The service owns entry, drafting, settlement and durable rewards.
internal static partial class NativeArena
{
    internal static long DeckId => DeckIdFor(Read());
    private static long DeckIdFor(ArenaView view)
    {
        if (string.IsNullOrEmpty(view.RunId)) return 0;
        using (var hash = SHA256.Create())
            return Math.Max(1, BitConverter.ToInt64(hash.ComputeHash(Encoding.UTF8.GetBytes(view.RunId)), 0) & long.MaxValue);
    }
    // Native slots count the hero as choice zero; the service's own slot remains opaque to the transport.
    private static int NativeSlot(ArenaView view) => view.Phase == ArenaPhase.None || view.Phase == ArenaPhase.ChoosingHero ? 0 : view.Deck.Count + 1;
    private sealed class Reply
    {
        internal int Id, Frame;
        internal string Accessor;
        internal object Payload;
        internal IArenaService Service;
        internal bool Display, Mutating;
    }
    private static readonly Queue<Reply> replies = new Queue<Reply>();
    private static readonly List<int> lastReplies = new List<int>();
    private static Reply delivering;
    private static ManualLogSource log;
    private static DraftManager loggedIn;
    private static string lastError;
    private static bool mutationPending, startingMatch;
    internal static bool MatchActive { get; private set; }
    private static bool Offline => NativeUIProbe.Active;
    private static bool InDraft => Offline && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.DRAFT;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        foreach (var pair in new[] {
            ("SendArenaSessionRequest", nameof(SessionRequest)), ("RequestDraftChoicesAndContents", nameof(ContentsRequest)),
            ("DraftBegin", nameof(BeginRequest)), ("MakeDraftChoice", nameof(ChoiceRequest)),
            ("DraftRetire", nameof(RetireRequest)), ("AckDraftRewards", nameof(AckRequest)),
            ("DraftRequestDisablePremiums", nameof(PremiumsRequest)), ("RequestArenaRatingInfo", nameof(RatingRequest)),
            ("SaveArenaDeck", nameof(SaveRequest)), ("RedraftBegin", nameof(RedraftRequest)),
            ("GetArenaSessionResponse", nameof(SessionReply)), ("GetDraftChoicesAndContents", nameof(ContentsReply)),
            ("GetBeginDraft", nameof(BeginReply)), ("GetDraftChosen", nameof(ChoiceReply)),
            ("GetRetiredDraft", nameof(RetireReply)), ("GetRewardsAckDraftID", nameof(AckReply)),
            ("GetDraftRemovePremiumsResponse", nameof(PremiumsReply)), ("GetArenaRatingInfoResponse", nameof(RatingReply)),
            ("GetSaveArenaDeckResponse", nameof(SaveReply)), ("GetDraftError", nameof(ErrorReply)) })
            Patch(harmony, typeof(Network), pair.Item1, pair.Item2);
        Patch(harmony, typeof(ArenaLandingPageManager), "Awake", nameof(BeforeScene));
        Patch(harmony, typeof(DraftDisplay), "Awake", nameof(BeforeScene));
        Patch(harmony, typeof(ArenaLandingPageManager), "SpendTicket", nameof(Enter));
        Patch(harmony, typeof(ArenaLandingPageManager), "ToggleUndergroundState", nameof(Underground));
        Patch(harmony, typeof(ArenaLandingPageManager), "ShowArenaDisclaimer", nameof(Disclaimer));
        Patch(harmony, typeof(ArenaLandingPageManager), "UpdateArenaRatingValues", nameof(NoRating));
        Patch(harmony, typeof(ArenaLandingPageManager), "StartMatchmaking", nameof(FindGame));
        Patch(harmony, typeof(DraftManager), "FindGame", nameof(FindGame));
        Patch(harmony, typeof(DraftDisplay), "PlayButtonPress", nameof(FindGame));
        Patch(harmony, typeof(DraftManager), "SetUndergroundState", nameof(NormalMode));
        Patch(harmony, typeof(DraftManager), "GetDraftPremium", nameof(NormalPremium));
        Patch(harmony, typeof(CollectionManager), "GetHeroPremium", nameof(DraftHeroPremium), typeof(TAG_CLASS));
        Patch(harmony, typeof(DraftManager), "GetNumTicketsOwned", nameof(Tickets));
        harmony.Patch(Required(typeof(CollectionDeck), "SendChanges"),
            prefix: new HarmonyMethod(typeof(NativeArena), nameof(ArenaDeckChanges)) { priority = Priority.First });
        Patch(harmony, typeof(DraftManager), "GetMaxWins", nameof(MaxWins), typeof(bool));
        Patch(harmony, typeof(DraftManager), "GetMaxWins", nameof(MaxWins), Type.EmptyTypes);
        Patch(harmony, typeof(DraftManager), "GetMaxLosses", nameof(MaxLosses), typeof(bool));
        Patch(harmony, typeof(DraftManager), "GetMaxLosses", nameof(MaxLosses), Type.EmptyTypes);
        Patch(harmony, typeof(ArenaModeToggleButton), "DetermineStartingState", nameof(ToggleStartingState));
        Patch(harmony, typeof(Box), "OnArenaButtonPressed", nameof(HomeButton));
        InstallRetailGuards(harmony);
        Patch(harmony, typeof(Hub), "HandleAreanaButtonPressed", nameof(HomeArena));
        // These calls have no offline backend. Keep the scope to this scene and our Arena match.
        Patch(harmony, typeof(BnetPresenceMgr), "SetGameFieldBlob", nameof(NoPresence));
        Patch(harmony, typeof(PresenceMgr), "SetStatus", nameof(NoPresence));
        Patch(harmony, typeof(PresenceMgr), "SetPrevStatus", nameof(NoPresence));
        Patch(harmony, typeof(AchieveManager), "IsReady", nameof(AchievementsReady));
        Patch(harmony, typeof(TavernTicketDisplay), "OpenTavernTicketProductPage", nameof(NoStore));
        harmony.Patch(Required(typeof(DraftManager), "OnLoggedIn"), postfix: new HarmonyMethod(typeof(NativeArena), nameof(LoggedIn)));
        harmony.Patch(Required(typeof(GameMgr), "GetPostGameSceneMode"), postfix: new HarmonyMethod(typeof(NativeArena), nameof(ReturnToDraft)));
    }

    private static MethodInfo Required(Type owner, string name, Type[] args = null) =>
        AccessTools.Method(owner, name, args) ?? throw new MissingMethodException(owner.FullName, name);
    private static void Patch(Harmony harmony, Type owner, string name, string prefix, params Type[] args) =>
        harmony.Patch(Required(owner, name, args.Length == 0 && name != "GetMaxWins" && name != "GetMaxLosses" ? null : args), prefix: new HarmonyMethod(typeof(NativeArena), prefix));

    internal static void Bootstrap()
    {
        var manager = DraftManager.Get() ?? throw new InvalidOperationException("DraftManager service missing.");
        if (!ReferenceEquals(loggedIn, manager)) manager.OnLoggedIn();
        SeedSession(manager);
    }
    private static void LoggedIn(DraftManager __instance) { loggedIn = __instance; }
    internal static void PrepareScene()
    {
        Bootstrap();
        MatchActive = false;
    }
    private static void BeforeScene() { if (Offline) PrepareScene(); }
    private static void SeedSession(DraftManager manager)
    {
        manager.SetUndergroundState(false);
        manager.OnArenaSessionResponsePacket(BuildSession(Read()));
    }
    private static ArenaView Read() => ArenaServiceRegistry.Current?.View() ?? new ArenaView { EntryCost = 150 };
    private static IArenaService Service() => ArenaServiceRegistry.Current ?? throw new InvalidOperationException("Arena service unavailable.");
    private static bool HomeButton(Box __instance)
    {
        if (!Offline) return true;
        if (!NativeRewardUI.BlocksHome && !NativeAchievements.Active && !NativeProfileSelector.BlocksHome)
            __instance.FireButtonPressEvent(Box.ButtonType.ARENA);
        return false;
    }
    private static bool HomeArena()
    {
        if (!Offline) return true;
        NativeUIProbe.Navigate(SceneMgr.Mode.DRAFT);
        return false;
    }

    internal static void Update()
    {
        if (!Offline) return;
        UpdatePresentation();
        int count = replies.Count;
        for (int i = 0; i < count; i++)
        {
            Reply reply = replies.Dequeue();
            if (!ReferenceEquals(reply.Service, ArenaServiceRegistry.Current)) { if (reply.Mutating) mutationPending = false; continue; }
            if (reply.Frame >= Time.frameCount || reply.Display && DraftDisplay.Get() == null)
            {
                if (Time.frameCount - reply.Frame < 1800) replies.Enqueue(reply);
                else { if (reply.Mutating) mutationPending = false; Notice("Arena screen did not register its reply handlers."); }
                continue;
            }
            try
            {
                delivering = reply;
                if (!Network.Get().FakeHandleType(reply.Id)) throw new InvalidOperationException("Unhandled Arena reply " + reply.Id);
                lastReplies.Add(reply.Id);
                if (lastReplies.Count > 20) lastReplies.RemoveAt(0);
                log.LogInfo("NATIVE_ARENA reply=" + reply.Id + " phase=" + Read().Phase);
            }
            catch (Exception error) { Notice("Arena display failed: " + error.GetBaseException().Message); }
            finally { delivering = null; if (reply.Mutating) mutationPending = false; }
        }
    }
    private static void Queue(int id, string accessor, object payload, bool display = true)
    {
        replies.Enqueue(new Reply { Id = id, Accessor = accessor, Payload = payload, Frame = Time.frameCount,
            Service = ArenaServiceRegistry.Current, Display = display, Mutating = mutationPending && (id == 246 || id == 249 || id == 247 || id == 288) });
    }
    private static bool Result<T>(string accessor, ref T result)
    {
        if (!Offline || delivering?.Accessor != accessor) return true;
        result = (T)delivering.Payload;
        return false;
    }
    private static bool SessionReply(ref ArenaSessionResponse __result) => Result("GetArenaSessionResponse", ref __result);
    private static bool ContentsReply(ref Network.DraftChoicesAndContents __result) => Result("GetDraftChoicesAndContents", ref __result);
    private static bool BeginReply(ref Network.BeginDraft __result) => Result("GetBeginDraft", ref __result);
    private static bool ChoiceReply(ref Network.DraftChosen __result) => Result("GetDraftChosen", ref __result);
    private static bool RetireReply(ref Network.DraftRetired __result) => Result("GetRetiredDraft", ref __result);
    private static bool AckReply(ref long __result) => Result("GetRewardsAckDraftID", ref __result);
    private static bool PremiumsReply(ref Network.DraftChoicesAndContents __result) => Result("GetDraftRemovePremiumsResponse", ref __result);
    private static bool RatingReply(ref ArenaRatingInfoResponse __result) => Result("GetArenaRatingInfoResponse", ref __result);
    private static bool SaveReply(ref SaveArenaDeckResponse __result) => Result("GetSaveArenaDeckResponse", ref __result);
    private static bool ErrorReply(ref DraftError __result) => Result("GetDraftError", ref __result);

    private static void Notice(string message)
    {
        lastError = message;
        log?.LogWarning("NATIVE_ARENA " + message);
        DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo { m_id = "solostone.arena.notice", m_headerText = "Arena",
            m_text = message, m_responseDisplay = AlertPopup.ResponseDisplay.OK });
    }
    private static void Refuse(string message)
    {
        Notice(message);
        Queue(251, "GetDraftError", new DraftError { ErrorCode_ = DraftError.ErrorCode.DE_UNKNOWN });
        QueueContents(Read());
    }
    private static ArenaView Mutate(Func<IArenaService, ArenaView> action)
    {
        if (mutationPending) return null;
        try
        {
            ArenaView view = action(Service());
            if (view == null || !string.IsNullOrEmpty(view.Error)) { Refuse(view?.Error ?? "Arena returned no state."); return null; }
            lastError = null;
            mutationPending = true;
            return view;
        }
        catch (Exception error) { Refuse(error.GetBaseException().Message); return null; }
    }
    private static bool SessionRequest()
    {
        if (!Offline) return true;
        Queue(351, "GetArenaSessionResponse", BuildSession(Read()), false);
        return false;
    }
    private static void QueueContents(ArenaView view)
    {
        if (view.Phase == ArenaPhase.None)
            Queue(251, "GetDraftError", new DraftError { ErrorCode_ = DraftError.ErrorCode.DE_NOT_IN_DRAFT });
        else Queue(248, "GetDraftChoicesAndContents", BuildContents(view));
    }
    private static bool ContentsRequest(bool __0)
    {
        if (!Offline) return true;
        if (__0) { Notice("Underground coming later."); return false; }
        QueueContents(Read());
        return false;
    }
    private static ArenaView BeginRun()
    {
        var before = Read();
        if (mutationPending) return null;
        if (before.Phase != ArenaPhase.None) { Refuse("Finish the current Arena run first."); return null; }
        ArenaView view = Mutate(service => service.Begin(Guid.NewGuid().ToString("N")));
        if (view == null) return null;
        var begin = new Network.BeginDraft { DeckID = DeckIdFor(view), Wins = view.Wins, MaxSlot = view.MaxSlot,
            SlotType = DraftSlotType.DRAFT_SLOT_HERO, UniqueSlotTypesForDraft = SlotTypes(), Session = BuildRunSession(view) };
        begin.Heroes.AddRange(view.HeroOffers.Select(Card));
        Queue(246, "GetBeginDraft", begin);
        return view;
    }
    private static bool BeginRequest(bool __0)
    {
        if (!Offline) return true;
        if (__0) Notice("Underground coming later."); else BeginRun();
        return false;
    }
    private static bool Enter(ArenaLandingPageManager __instance)
    {
        if (!Offline) return true;
        if (BeginRun() != null)
        {
            __instance.GetWidget().TriggerEvent("MOVETO_DRAFTING");
            __instance.GetWidget().TriggerEvent("DRAFTING");
        }
        return false;
    }
    private static bool ChoiceRequest(long __0, int __1, int __2, int __3, bool __4, bool __5, List<int> __6)
    {
        if (!Offline) return true;
        if (mutationPending) return false;
        ArenaView before = Read();
        var offers = before.Phase == ArenaPhase.ChoosingHero ? before.HeroOffers : before.Offers;
        if (__0 != DeckId || __4 || __5 || __1 != NativeSlot(before) || __2 < 1 || __2 > offers.Count)
        { Refuse("Arena choice is stale. Try the current offers."); return false; }
        string chosen = offers[__2 - 1];
        ArenaView view = Mutate(service => service.Choose(before.Slot, __2 - 1));
        if (view != null)
            Queue(249, "GetDraftChosen", new Network.DraftChosen { ChosenCard = new Network.CardChoice { CardDef = Card(chosen) },
                SlotType = SlotType(view), NextChoices = Choices(view) });
        return false;
    }
    private static bool RetireRequest(long __0, int __1, int __2, bool __3)
    {
        if (!Offline) return true;
        if (__0 != DeckId || __3) { Refuse("Arena run is stale."); return false; }
        ArenaView view = Mutate(service => service.Retire());
        if (view != null) Queue(247, "GetRetiredDraft", new Network.DraftRetired { Deck = DeckId, Chest = Chest(view.Reward) });
        return false;
    }
    private static bool AckRequest(long __0, int __1, bool __2)
    {
        if (!Offline) return true;
        if (__0 != DeckId || __2) { Refuse("Arena reward is stale."); return false; }
        ArenaView before = Read();
        ArenaView view = Mutate(service => service.AcknowledgeReward(before.RunId));
        if (view != null) Queue(288, "GetRewardsAckDraftID", DeckIdFor(before), false);
        return false;
    }
    private static bool PremiumsRequest()
    {
        if (!Offline) return true;
        Queue(355, "GetDraftRemovePremiumsResponse", BuildContents(Read()));
        return false;
    }
    private static bool RatingRequest()
    {
        if (!Offline) return true;
        var view = Read();
        Queue(719, "GetArenaRatingInfoResponse", new ArenaRatingInfoResponse { PlayerInfo = new ArenaPlayerInfo {
            NormalWins = view.Wins, NormalLosses = view.Losses, NormalMaxWins = 12, NormalMaxLosses = 3,
            UndergroundMaxWins = 12, UndergroundMaxLosses = 3 } }, false);
        return false;
    }
    private static bool ArenaDeckChanges(CollectionDeck __instance, CollectionDeck.ChangeSource __0)
    {
        if (!Offline || __instance.Type != DeckType.DRAFT_DECK) return true;
        // Arena autosaves never enter the constructed editor, its ownership validation, or the login-gated sender.
        if (__0 == CollectionDeck.ChangeSource.Arena)
            SaveRequest(__instance.ID, null, 0, 0, null, false);
        return false;
    }
    private static bool SaveRequest(long __0, List<Network.CardUserData> __1, long __2, int __3, RuneType[] __4, bool __5)
    {
        if (!Offline) return true;
        // MVP decks are immutable: answer with the authoritative deck, never persist an editor's reconstruction.
        if (__0 != DeckId || __5) { Refuse("Arena deck is stale."); return false; }
        var deck = new PegasusUtil.DeckContents { DeckId = DeckId };
        foreach (var group in Read().Deck.GroupBy(id => id))
            deck.Cards.Add(new DeckCardData { Def = new PegasusShared.CardDef { Asset = GameUtils.TranslateCardIdToDbId(group.Key), Premium = 0 }, Qty = group.Count() });
        Queue(716, "GetSaveArenaDeckResponse", new SaveArenaDeckResponse { Decks = deck });
        return false;
    }
    private static bool RedraftRequest() { if (!Offline) return true; Refuse("Redraft is unavailable in this Arena mode."); return false; }

    private static bool FindGame()
    {
        if (!Offline) return true;
        if (startingMatch || mutationPending || DraftDisplay.Get()?.IsRewardsFlowPending == true) return false;
        startingMatch = true;
        try
        {
            if (!Service().StartMatch(out string error)) Notice(error ?? "Arena match could not start.");
            else MatchActive = true;
        }
        catch (Exception error) { Notice(error.GetBaseException().Message); }
        finally { startingMatch = false; }
        return false;
    }
    // Keep GT_VS_AI transport/presentation. Only post-game routing changes; retail's OnGameOver must not count a second result.
    internal static SceneMgr.Mode NavigationTarget(SceneMgr.Mode mode) => Offline && MatchActive && mode == SceneMgr.Mode.TOURNAMENT ? SceneMgr.Mode.DRAFT : mode;
    private static void ReturnToDraft(ref SceneMgr.Mode __result) { if (Offline && MatchActive) __result = SceneMgr.Mode.DRAFT; }
    private static bool Underground() { if (!Offline) return true; Notice("Underground coming later."); return false; }
    private static bool Disclaimer() { if (!Offline) return true; Notice("Arena: 12 wins or 3 losses. Entry costs " + Read().EntryCost + " gold. Underground coming later."); return false; }
    private static void NormalMode(ref bool __0) { if (Offline) __0 = false; }
    private static bool NormalPremium(ref TAG_PREMIUM __result) { if (!Offline) return true; __result = TAG_PREMIUM.NORMAL; return false; }
    private static bool DraftHeroPremium(ref TAG_PREMIUM __result) { if (!InDraft) return true; __result = TAG_PREMIUM.NORMAL; return false; }
    private static bool Tickets(ref int __result) { if (!Offline) return true; __result = 1; return false; }
    private static bool MaxWins(ref int __result) { if (!Offline) return true; __result = 12; return false; }
    private static bool MaxLosses(ref int __result) { if (!Offline) return true; __result = 3; return false; }
    private static bool NoPresence() => !Offline || !InDraft && !MatchActive;
    private static bool AchievementsReady(ref bool __result) { if (!InDraft) return true; __result = true; return false; }
    private static bool NoStore() { if (!InDraft) return true; Notice("Entry costs " + Read().EntryCost + " gold. Use Enter Arena."); return false; }
    private static bool ToggleStartingState(ArenaModeToggleButton __instance)
    {
        if (!Offline) return true;
        __instance.GetComponent<Hearthstone.UI.Widget>()?.TriggerEvent("START_DEFAULT");
        return false;
    }

    internal static ArenaSessionResponse BuildSession(ArenaView view) => new ArenaSessionResponse {
        ErrorCode = ErrorCode.ERROR_OK, CurrentSeason = Season(), Session = BuildRunSession(view),
        UndergroundSession = new ArenaSession { State = ArenaSessionState.NO_RUN, IsActive = false, IsUnderground = true, Info = Season() } };
    private static ArenaSeasonInfo Season() => new ArenaSeasonInfo { CurrentSeasonDisplayId = 1, IsDualClass = false,
        Season = new ArenaSeasonSpec { GameContentSeason = new GameContentSeasonSpec { SeasonId = 1, EndSecondsFromNow = uint.MaxValue } } };
    private static ArenaSession BuildRunSession(ArenaView view) => new ArenaSession { Wins = view.Wins, Losses = view.Losses,
        IsActive = view.Phase != ArenaPhase.None, IsInRewards = view.Phase == ArenaPhase.RewardPending, IsUnderground = false,
        State = view.Phase == ArenaPhase.None ? ArenaSessionState.NO_RUN : view.Phase == ArenaPhase.Ready ? ArenaSessionState.MIDRUN
            : view.Phase == ArenaPhase.RewardPending ? ArenaSessionState.REWARDS : ArenaSessionState.DRAFTING, Info = Season() };
    private static DraftSlotType SlotType(ArenaView view) => view.Phase == ArenaPhase.ChoosingHero ? DraftSlotType.DRAFT_SLOT_HERO : DraftSlotType.DRAFT_SLOT_CARD;
    private static List<DraftSlotType> SlotTypes() => new List<DraftSlotType> { DraftSlotType.DRAFT_SLOT_HERO, DraftSlotType.DRAFT_SLOT_CARD };
    private static NetCache.CardDefinition Card(string id) => new NetCache.CardDefinition { Name = id ?? string.Empty, Premium = TAG_PREMIUM.NORMAL };
    private static List<Network.CardChoice> Choices(ArenaView view) => (view.Phase == ArenaPhase.ChoosingHero ? view.HeroOffers : view.Offers)
        .Select(id => new Network.CardChoice { CardDef = Card(id) }).ToList();
    internal static Network.DraftChoicesAndContents BuildContents(ArenaView view)
    {
        var contents = new Network.DraftChoicesAndContents { Slot = NativeSlot(view), RedraftSlot = -1, MaxSlot = view.MaxSlot,
            MaxWins = 12, MaxLosses = 3, Wins = view.Wins, Losses = view.Losses,
            SlotType = SlotType(view), Session = BuildRunSession(view), Chest = Chest(view.Reward) };
        contents.DeckInfo.Deck = DeckIdFor(view);
        contents.Hero.Name = view.HeroCardId ?? string.Empty;
        contents.HeroPower.Name = string.IsNullOrEmpty(view.HeroCardId) ? string.Empty : GameUtils.GetHeroPowerCardIdFromHero(view.HeroCardId);
        contents.Choices.AddRange(Choices(view));
        contents.UniqueSlotTypesForDraft.AddRange(SlotTypes());
        foreach (var group in view.Deck.GroupBy(id => id)) contents.DeckInfo.Cards.Add(new Network.CardUserData {
            DbId = GameUtils.TranslateCardIdToDbId(group.Key), Count = group.Count(), Premium = TAG_PREMIUM.NORMAL });
        return contents;
    }
    private static Network.RewardChest Chest(ArenaReward reward)
    {
        if (reward == null) return null;
        var chest = new Network.RewardChest();
        if (reward.Gold > 0) chest.Rewards.Add(new GoldRewardData(reward.Gold));
        if (reward.Dust > 0) chest.Rewards.Add(new ArcaneDustRewardData(reward.Dust));
        foreach (var pack in reward.PackTypes.GroupBy(type => type)) chest.Rewards.Add(new BoosterPackRewardData(FairPackRules.NativeBoosterId(pack.Key), pack.Count()));
        return chest;
    }
}

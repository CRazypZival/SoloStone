namespace NativeArenaBoundary
{
using PegasusShared;
using PegasusUtil;

internal static partial class NativeArena
{
    private static void UpdatePresentation() { }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Arena: " + message); }
    internal static void RunTests()
    {
        log = new ManualLogSource();
        var stub = new ArenaStubService(_ => new[] { "CARD_A", "CARD_B", "CARD_C" });
        ArenaServiceRegistry.Current = stub;
        NativeUIProbe.Active = false;
        Check(SessionRequest() && BeginRequest(false) && ContentsRequest(false) && RedraftRequest(), "inactive sends keep retail behavior");
        Check(stub.View().Phase == ArenaPhase.None, "inactive transport leaves service unchanged");
        NativeUIProbe.Active = true;
        var received = new List<(int Id, object Payload)>();
        Network.Receive = id => {
            object value = null;
            switch (id)
            {
                case 351: ArenaSessionResponse session = null; Check(!SessionReply(ref session), "session accessor"); value = session; break;
                case 246: Network.BeginDraft begin = null; Check(!BeginReply(ref begin), "begin accessor"); value = begin; break;
                case 248: Network.DraftChoicesAndContents contents = null; Check(!ContentsReply(ref contents), "contents accessor"); value = contents; break;
                case 249: Network.DraftChosen chosen = null; Check(!ChoiceReply(ref chosen), "chosen accessor"); value = chosen; break;
                case 247: Network.DraftRetired retired = null; Check(!RetireReply(ref retired), "retire accessor"); value = retired; break;
                case 288: long deck = 0; Check(!AckReply(ref deck), "ack accessor"); value = deck; break;
                case 355: Network.DraftChoicesAndContents premiums = null; Check(!PremiumsReply(ref premiums), "premium accessor"); value = premiums; break;
                case 719: ArenaRatingInfoResponse rating = null; Check(!RatingReply(ref rating), "rating accessor"); value = rating; break;
                case 716: SaveArenaDeckResponse save = null; Check(!SaveReply(ref save), "save accessor"); value = save; break;
                case 251: DraftError error = null; Check(!ErrorReply(ref error), "error accessor"); value = error; break;
                default: throw new Exception("unexpected reply " + id);
            }
            received.Add((id, value));
        };
        void Tick() { Time.frameCount++; Update(); }
        ContentsRequest(false); Update();
        Check(received.Count == 0, "same-frame replies must wait"); Tick();
        Check(((DraftError)received.Last().Payload).ErrorCode_ == DraftError.ErrorCode.DE_NOT_IN_DRAFT, "empty run opens landing, not active deck");
        SessionRequest(); Tick();
        var sessionReply = (ArenaSessionResponse)received.Last().Payload;
        Check(sessionReply.CurrentSeason.Season.GameContentSeason.EndSecondsFromNow > 0
            && sessionReply.Session.State == ArenaSessionState.NO_RUN && !sessionReply.UndergroundSession.IsActive, "season exists before scene and only one playable mode");
        // Queue a session before Begin: its delivery must not release the mutation lock for the pending Begin.
        SessionRequest(); BeginRequest(false);
        Check(stub.View().GoldBalance == 850 && stub.View().HeroOffers.Count == 3 && mutationPending, "Begin pays once through service");
        BeginRequest(false); Check(replies.Count == 2, "double-click cannot begin or enqueue twice");
        DraftDisplay.Current = null; Tick();
        Check(mutationPending && replies.Count == 1, "session reply cannot unlock pending display mutation");
        DraftDisplay.Current = new DraftDisplay(); Tick();
        var beginReply = (Network.BeginDraft)received.Last().Payload;
        Check(beginReply.DeckID == DeckId && beginReply.MaxSlot == 30 && beginReply.Heroes.Select(card => card.Name)
            .SequenceEqual(new[] { "HERO_01", "HERO_08", "HERO_06" }), "native hero offers and deck id");
        var detached = stub.View(); detached.HeroOffers.Clear(); Check(stub.View().HeroOffers.Count == 3, "stub views are detached");
        ChoiceRequest(DeckId, 0, 2, 3, false, false, new List<int>()); Tick();
        var heroReply = (Network.DraftChosen)received.Last().Payload;
        Check(stub.View().HeroCardId == "HERO_08" && stub.View().Slot == 1 && heroReply.ChosenCard.CardDef.Premium == TAG_PREMIUM.NORMAL
            && heroReply.NextChoices.Count == 3 && heroReply.SlotType == DraftSlotType.DRAFT_SLOT_CARD, "one-based native hero index maps to zero-based service and normal premium");
        ChoiceRequest(DeckId, 0, 1, 0, false, false, new List<int>()); Tick();
        Check(stub.View().Deck.Count == 0 && lastError != null, "stale slot changes nothing and refreshes current offers");
        for (int i = 0; i < 30; i++)
        {
            ChoiceRequest(DeckId, stub.View().Slot, 1, 1, false, false, new List<int>());
            ChoiceRequest(DeckId, stub.View().Slot, 1, 1, false, false, new List<int>()); // pending duplicate
            Tick();
        }
        Check(stub.View().Phase == ArenaPhase.Ready && stub.View().Slot == 31 && stub.View().Deck.Count == 30, "hero plus exactly thirty card picks");
        var translated = stub.View(); translated.Slot = 999;
        Check(BuildContents(translated).Slot == 31, "native slots do not depend on service slot numbering");
        long firstDeckId = DeckId;
        Check(firstDeckId != DeckIdFor(new ArenaView { RunId = "another-run" }), "deck ids distinguish successive runs");
        ContentsRequest(false); Tick();
        var ready = (Network.DraftChoicesAndContents)received.Last().Payload;
        Check(ready.Slot == 31 && ready.MaxSlot == 30 && ready.MaxWins == 12 && ready.MaxLosses == 3 && ready.Choices.Count == 0
            && ready.DeckInfo.Cards.Sum(card => card.Count) == 30 && ready.DeckInfo.Cards.Max(card => card.Count) == 30
            && ready.DeckInfo.Cards.All(card => card.Premium == TAG_PREMIUM.NORMAL), "ready deck keeps duplicates beyond constructed limits");
        RatingRequest(); Tick();
        var info = ((ArenaRatingInfoResponse)received.Last().Payload).PlayerInfo;
        Check(info.NormalMaxWins == 12 && info.NormalMaxLosses == 3 && info.Rating == 0, "record reply has no rating");
        PremiumsRequest(); Tick(); Check(received.Last().Id == 355, "disable premiums returns authoritative normal deck");
        SaveRequest(DeckId, new List<Network.CardUserData>(), 0, 0, Array.Empty<RuneType>(), false); Tick();
        Check(((SaveArenaDeckResponse)received.Last().Payload).Decks.Cards.Sum(card => card.Qty) == 30 && stub.View().Deck.Count == 30,
            "save cannot replace authoritative drafted deck with editor input");
        Check(ArenaDeckChanges(new CollectionDeck { Type = DeckType.NORMAL_DECK }, CollectionDeck.ChangeSource.Arena), "constructed saves retain existing path");
        Check(!ArenaDeckChanges(new CollectionDeck { ID = DeckId, Type = DeckType.DRAFT_DECK }, CollectionDeck.ChangeSource.Arena), "Arena editor save bypasses constructed validation and login gate");
        Tick(); Check(received.Last().Id == 716, "Arena editor save gets authoritative reply");
        RedraftRequest(); Tick(); Check(stub.View().Phase == ArenaPhase.Ready, "redraft refused");
        Underground(); Check(DialogManager.Last.m_text.Contains("coming later") && stub.View().Phase == ArenaPhase.Ready, "Underground explains and never switches");
        bool underground = true; NormalMode(ref underground); Check(!underground, "retail preference cannot enable Underground");
        FindGame(); Check(lastError == "stub" && !MatchActive, "stub never launches a match");
        RetireRequest(DeckId, 31, 1, false); Tick();
        var chest = ((Network.DraftRetired)received.Last().Payload).Chest;
        Check(chest.Rewards.OfType<GoldRewardData>().Single().Amount == 100 && chest.Rewards.OfType<ArcaneDustRewardData>().Single().Amount == 10
            && chest.Rewards.OfType<BoosterPackRewardData>().Single().Count == 1, "native chest uses service reward");
        Check(stub.View().GoldBalance == 950 && stub.Retire().GoldBalance == 950, "stub reward is paid before presentation and repeated retire cannot pay twice");
        AckRequest(DeckId, 31, false); Tick(); Check(stub.View().Phase == ArenaPhase.None && received.Last().Id == 288 && stub.View().GoldBalance == 950,
            "claim acknowledges service reward without paying again");
        // Old-profile replies cannot arrive in a newly bound service, even when the slot numbers happen to match.
        ContentsRequest(false); ArenaServiceRegistry.Current = new ArenaStubService(_ => new[] { "A", "B", "C" });
        int delivered = received.Count; Tick(); Check(received.Count == delivered, "replies are scoped to service identity");
        Network.BeginDraft outside = null; Check(BeginReply(ref outside), "stash is available only during dispatch");
        MatchActive = true; var mode = SceneMgr.Mode.TOURNAMENT; ReturnToDraft(ref mode); Check(mode == SceneMgr.Mode.DRAFT, "Arena returns to draft with VS_AI transport");
        Check(NavigationTarget(SceneMgr.Mode.TOURNAMENT) == SceneMgr.Mode.DRAFT && NavigationTarget(SceneMgr.Mode.HUB) == SceneMgr.Mode.HUB,
            "post-reward picker navigation returns to Arena without changing other destinations");
        MatchActive = false; mode = SceneMgr.Mode.TOURNAMENT; ReturnToDraft(ref mode); Check(mode == SceneMgr.Mode.TOURNAMENT, "other modes preserve route");
        ArenaServiceRegistry.Current = null; NativeUIProbe.Active = false;
        Console.WriteLine("PASS Arena transport: deferred/scoped replies, ten send paths, 30 picks, no copy cap, errors, chest, claim, return");
    }
}

internal sealed class ManualLogSource { internal void LogInfo(string value) { } internal void LogWarning(string value) { } }
internal static class Time { internal static int frameCount; }
internal static class NativeUIProbe { internal static bool Active; }
internal sealed class SceneMgr
{
    internal enum Mode { HUB, TOURNAMENT, DRAFT, GAMEPLAY }
    internal static SceneMgr Get() => new SceneMgr();
    internal static bool IsInitialized() => true;
    internal Mode GetMode() => Mode.DRAFT;
}
internal sealed class DraftDisplay
{
    internal static DraftDisplay Current = new DraftDisplay();
    internal static DraftDisplay Get() => Current;
    internal bool IsRewardsFlowPending;
}
internal sealed class Widget { internal void TriggerEvent(string value) { } }
internal sealed class ArenaLandingPageManager { internal Widget GetWidget() => new Widget(); }
internal sealed class ArenaModeToggleButton { internal T GetComponent<T>() where T : new() => new T(); }
internal sealed class AlertPopup
{
    internal enum ResponseDisplay { OK }
    internal sealed class PopupInfo { internal string m_id, m_headerText, m_text; internal ResponseDisplay m_responseDisplay; }
}
internal sealed class DialogManager
{
    internal static AlertPopup.PopupInfo Last;
    internal static DialogManager Get() => new DialogManager();
    internal void ShowPopup(AlertPopup.PopupInfo info) { Last = info; }
}
internal static class FairPackRules { internal static int NativeBoosterId(string type) => 1; }
internal static class GameUtils
{
    internal static int TranslateCardIdToDbId(string id) => id[id.Length - 1];
    internal static string GetHeroPowerCardIdFromHero(string hero) => "POWER_" + hero;
}
internal enum DeckType { NORMAL_DECK, DRAFT_DECK }
internal sealed class CollectionDeck { internal enum ChangeSource { Arena } internal long ID; internal DeckType Type; }
internal enum TAG_PREMIUM { NORMAL, GOLDEN }
internal abstract class RewardData { }
internal sealed class GoldRewardData(int amount) : RewardData { internal int Amount = amount; }
internal sealed class ArcaneDustRewardData(int amount) : RewardData { internal int Amount = amount; }
internal sealed class BoosterPackRewardData(int id, int count) : RewardData { internal int Id = id, Count = count; }
internal static class NetCache { internal sealed class CardDefinition { internal string Name; internal TAG_PREMIUM Premium; } }
internal sealed class Network
{
    internal static Action<int> Receive;
    internal static Network Get() => new Network();
    internal bool FakeHandleType(int id) { Receive(id); return true; }
    internal sealed class CardChoice { internal NetCache.CardDefinition CardDef; }
    internal sealed class CardUserData { internal int DbId, Count; internal TAG_PREMIUM Premium; }
    internal sealed class DeckContents { internal long Deck; internal List<CardUserData> Cards = new(); }
    internal sealed class BeginDraft
    {
        internal long DeckID; internal int Wins, MaxSlot; internal DraftSlotType SlotType; internal List<DraftSlotType> UniqueSlotTypesForDraft;
        internal ArenaSession Session; internal List<NetCache.CardDefinition> Heroes = new();
    }
    internal sealed class DraftChoicesAndContents
    {
        internal int Slot, RedraftSlot, MaxSlot, MaxWins, MaxLosses, Wins, Losses; internal DraftSlotType SlotType; internal ArenaSession Session;
        internal RewardChest Chest; internal DeckContents DeckInfo = new(); internal NetCache.CardDefinition Hero = new(), HeroPower = new();
        internal List<CardChoice> Choices = new(); internal List<DraftSlotType> UniqueSlotTypesForDraft = new();
    }
    internal sealed class DraftChosen { internal CardChoice ChosenCard; internal DraftSlotType SlotType; internal List<CardChoice> NextChoices; }
    internal sealed class DraftRetired { internal long Deck; internal RewardChest Chest; }
    internal sealed class RewardChest { internal List<RewardData> Rewards = new(); }
}
}

namespace NativeArenaBoundary.PegasusShared
{
internal enum ArenaSessionState { NO_RUN, DRAFTING, MIDRUN, REWARDS }
internal enum DraftSlotType { DRAFT_SLOT_HERO, DRAFT_SLOT_CARD }
internal enum RuneType { }
internal enum ErrorCode { ERROR_OK }
internal sealed class GameContentSeasonSpec { internal int SeasonId; internal ulong EndSecondsFromNow; }
internal sealed class ArenaSeasonSpec { internal GameContentSeasonSpec GameContentSeason; }
internal sealed class CardDef { internal int Asset, Premium; }
}
namespace NativeArenaBoundary.PegasusUtil
{
using PegasusShared;
internal sealed class ArenaSeasonInfo { internal int CurrentSeasonDisplayId; internal bool IsDualClass; internal ArenaSeasonSpec Season; }
internal sealed class ArenaSession { internal int Wins, Losses; internal bool IsActive, IsInRewards, IsUnderground; internal ArenaSessionState State; internal ArenaSeasonInfo Info; }
internal sealed class ArenaSessionResponse { internal ErrorCode ErrorCode; internal ArenaSeasonInfo CurrentSeason; internal ArenaSession Session, UndergroundSession; }
internal sealed class ArenaRatingInfoResponse { internal ArenaPlayerInfo PlayerInfo; }
internal sealed class ArenaPlayerInfo { internal int NormalWins, NormalLosses, NormalMaxWins, NormalMaxLosses, UndergroundMaxWins, UndergroundMaxLosses, Rating; }
internal sealed class SaveArenaDeckResponse { internal DeckContents Decks; }
internal sealed class DeckContents { internal long DeckId; internal List<DeckCardData> Cards = new(); }
internal sealed class DeckCardData { internal CardDef Def; internal int Qty; }
internal sealed class DraftError { internal enum ErrorCode { DE_UNKNOWN, DE_NOT_IN_DRAFT } internal ErrorCode ErrorCode_; }
}

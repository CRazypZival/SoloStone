using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ArenaPick
{
    public List<string> Offers = new List<string>();
    public int Index;
    internal ArenaPick Copy() => new ArenaPick { Offers = Offers.ToList(), Index = Index };
}

public sealed class ArenaRun
{
    public string RunId;
    public long Seed;
    public int RulesVersion;
    public string GrindTier;
    public ArenaPhase Phase;
    public List<string> HeroOffers = new List<string>();
    public string Hero;
    public int HeroIndex = -1;
    public List<ArenaPick> Picks = new List<ArenaPick>();
    public List<string> Deck = new List<string>();
    public int Wins, Losses;
    public int EntryGold;
    public string EntryRequestId;
    public ArenaReward Reward;
    public bool Retired;

    public ArenaRun Copy() => new ArenaRun {
        RunId = RunId, Seed = Seed, RulesVersion = RulesVersion, GrindTier = GrindTier, Phase = Phase,
        HeroOffers = HeroOffers.ToList(), Hero = Hero, HeroIndex = HeroIndex,
        Picks = Picks.Select(pick => pick.Copy()).ToList(), Deck = Deck.ToList(), Wins = Wins, Losses = Losses,
        EntryGold = EntryGold, EntryRequestId = EntryRequestId, Reward = CopyReward(Reward), Retired = Retired
    };

    internal static ArenaReward CopyReward(ArenaReward reward) => reward == null ? null : new ArenaReward {
        RunId = reward.RunId, Wins = reward.Wins, Gold = reward.Gold, Dust = reward.Dust, PackTypes = reward.PackTypes.ToList()
    };

    // Pure transition. The caller credits Reward and stores this returned record in the same profile commit.
    public ArenaRun SettleMatch(string result, string packType)
    {
        Validate();
        if (Phase != ArenaPhase.Ready) throw new InvalidOperationException("Arena run is not ready.");
        var next = Copy();
        if (result == "Won") next.Wins++;
        else if (result == "Lost" || result == "Conceded" || result == "Abandoned") next.Losses++;
        else if (result != "Tied") throw new InvalidOperationException("Invalid Arena match result.");
        var rules = DraftRules.Arena(RulesVersion);
        if (next.Wins == rules.MaxWins || next.Losses == rules.MaxLosses) next.End(false, packType);
        next.Validate();
        return next;
    }

    internal void End(bool retired, string packType)
    {
        Retired = retired; Phase = ArenaPhase.RewardPending;
        Reward = DraftRules.Arena(RulesVersion).Reward(RunId, Wins, GrindTier, packType);
    }

    internal void Validate()
    {
        var rules = DraftRules.Arena(RulesVersion);
        if (!Guid.TryParseExact(RunId, "D", out _) || !ValidRequest(EntryRequestId) || !GrindTiers.IsKnown(GrindTier)
            || EntryGold != rules.EntryGold || HeroOffers == null || Picks == null || Deck == null
            || !HeroOffers.SequenceEqual(rules.HeroOffers(Seed)) || Picks.Count > rules.DeckSize
            || Wins < 0 || Wins > rules.MaxWins || Losses < 0 || Losses > rules.MaxLosses
            || Wins == rules.MaxWins && Losses == rules.MaxLosses
            || Retired && (Wins == rules.MaxWins || Losses == rules.MaxLosses)
            || Phase == ArenaPhase.None || !Enum.IsDefined(typeof(ArenaPhase), Phase)) Invalid();
        if (Hero == null) {
            if (HeroIndex != -1 || Picks.Count != 0 || Deck.Count != 0
                || Phase != ArenaPhase.ChoosingHero && Phase != ArenaPhase.RewardPending) Invalid();
        } else if (HeroIndex < 0 || HeroIndex >= rules.OfferSize || Hero != HeroOffers[HeroIndex]
            || Phase == ArenaPhase.ChoosingHero) Invalid();
        if (Deck.Count != Picks.Count) Invalid();
        var preceding = new List<string>();
        for (int i = 0; i < Picks.Count; i++) {
            var pick = Picks[i];
            if (pick == null || pick.Offers == null || pick.Index < 0 || pick.Index >= rules.OfferSize
                || !pick.Offers.SequenceEqual(rules.Offers(Seed, i + 1, Hero, preceding))
                || Deck[i] != pick.Offers[pick.Index]) Invalid();
            preceding.Add(Deck[i]);
        }
        if (Phase == ArenaPhase.Drafting && (Hero == null || Picks.Count >= rules.DeckSize)
            || Phase == ArenaPhase.Ready && Picks.Count != rules.DeckSize
            || Phase != ArenaPhase.RewardPending && (Reward != null || Retired || Wins == rules.MaxWins || Losses == rules.MaxLosses)
            || Picks.Count < rules.DeckSize && (Wins != 0 || Losses != 0)) Invalid();
        if (Phase == ArenaPhase.RewardPending) {
            if (!Retired && Wins != rules.MaxWins && Losses != rules.MaxLosses) Invalid();
            ValidateReward(Reward, RunId, Wins, GrindTier, RulesVersion);
        }
    }

    internal static bool ValidRequest(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
    internal static void Invalid() => throw new InvalidOperationException("Invalid Arena run record.");
    internal static void ValidateReward(ArenaReward reward, string runId, int wins, string tier, int version)
    {
        if (reward == null || reward.PackTypes == null || reward.PackTypes.Count == 0) Invalid();
        var expected = DraftRules.Arena(version).Reward(runId, wins, tier, reward.PackTypes[0]);
        if (reward.RunId != runId || reward.Wins != wins || reward.Gold != expected.Gold || reward.Dust != expected.Dust
            || !reward.PackTypes.SequenceEqual(expected.PackTypes)) Invalid();
    }
}

public sealed class ArenaReceipt
{
    public string RunId;
    public string EntryRequestId;
    public int RulesVersion;
    public string GrindTier;
    public int Wins, Losses;
    public ArenaReward Reward;
    public bool Retired;
    internal ArenaReceipt Copy() => new ArenaReceipt { RunId = RunId, EntryRequestId = EntryRequestId,
        RulesVersion = RulesVersion, GrindTier = GrindTier, Wins = Wins, Losses = Losses,
        Reward = ArenaRun.CopyReward(Reward), Retired = Retired };
}

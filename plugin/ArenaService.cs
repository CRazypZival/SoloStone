using System;
using System.Collections.Generic;
using System.Linq;

public sealed class ArenaService : IArenaService
{
    private readonly LocalProfile profile;
    public ArenaService(LocalProfile profile) => this.profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ArenaView View()
    {
        var run = profile.ArenaRun;
        var rules = DraftRules.Arena(run?.RulesVersion ?? 1);
        return new ArenaView {
            Phase = run?.Phase ?? ArenaPhase.None, RunId = run?.RunId, Wins = run?.Wins ?? 0, Losses = run?.Losses ?? 0,
            MaxWins = rules.MaxWins, MaxLosses = rules.MaxLosses, MaxSlot = rules.DeckSize,
            Slot = run == null || run.Hero == null ? 0 : run.Picks.Count + (run.Phase == ArenaPhase.Drafting ? 1 : 0),
            HeroCardId = run?.Hero, HeroOffers = run?.HeroOffers.ToList() ?? new List<string>(),
            Offers = run?.Phase == ArenaPhase.Drafting ? rules.Offers(run.Seed, run.Picks.Count + 1, run.Hero, run.Deck).ToList() : new List<string>(),
            Deck = run?.Deck.ToList() ?? new List<string>(), Reward = ArenaRun.CopyReward(run?.Reward),
            EntryCost = rules.EntryGold, GoldBalance = profile.Gold
        };
    }

    private ArenaView Error(string error) { var view = View(); view.Error = error; return view; }
    private ArenaView Change(Action<LocalProfileData> change)
    {
        try { profile.CommitChange(next => { change(next); return true; }); return View(); }
        catch (Exception error) { return Error(error.Message); }
    }

    public ArenaView Begin(string requestId)
    {
        if (!ArenaRun.ValidRequest(requestId)) return Error("Invalid entry request id.");
        if (profile.ArenaRequestSeen(requestId)) return View();
        if (profile.ArenaRun != null) return Error("An Arena run is already active.");
        if (profile.Pending != null) return Error("A match is pending.");
        if (profile.HasPendingRewardPresentation || profile.PackRevealPending) return Error("A reward presentation is pending.");
        var rules = DraftRules.Arena();
        if (profile.Gold < rules.EntryGold) return Error("Not enough gold.");
        return Change(next => {
            if (next.ArenaEntryRequestIds.Count >= LocalProfile.MaximumArenaRequests) throw new InvalidOperationException("Arena entry receipt limit reached.");
            string id = Guid.NewGuid().ToString("D");
            long seed = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
            if (!profile.InfiniteGold) next.GoldBalance = checked(next.GoldBalance - rules.EntryGold);
            next.ArenaRun = new ArenaRun { RunId = id, Seed = seed, RulesVersion = rules.Version, GrindTier = profile.GrindTier.Id,
                Phase = ArenaPhase.ChoosingHero, HeroOffers = rules.HeroOffers(seed).ToList(), EntryGold = rules.EntryGold, EntryRequestId = requestId };
            next.ArenaEntryRequestIds.Add(requestId);
        });
    }

    public ArenaView Choose(int slot, int index)
    {
        var run = profile.ArenaRun;
        if (run == null) return Error("No Arena run.");
        var rules = DraftRules.Arena(run.RulesVersion);
        if (index < 0 || index >= rules.OfferSize) return Error("Invalid choice index.");
        int expected = run.Hero == null ? 0 : run.Picks.Count + 1;
        if (expected > 0 && slot == expected - 1 && (slot == 0 ? run.HeroIndex : run.Picks[slot - 1].Index) == index) return View();
        if (slot != expected || run.Phase != ArenaPhase.ChoosingHero && run.Phase != ArenaPhase.Drafting)
            return Error("Invalid choice slot.");
        return Change(next => {
            var current = next.ArenaRun;
            if (slot == 0) { current.HeroIndex = index; current.Hero = current.HeroOffers[index]; current.Phase = ArenaPhase.Drafting; }
            else {
                var offers = rules.Offers(current.Seed, slot, current.Hero, current.Deck).ToList();
                current.Picks.Add(new ArenaPick { Offers = offers, Index = index }); current.Deck.Add(offers[index]);
                if (current.Picks.Count == rules.DeckSize) current.Phase = ArenaPhase.Ready;
            }
        });
    }

    public ArenaView Retire()
    {
        var run = profile.ArenaRun;
        if (run == null) return Error("No Arena run.");
        if (run.Phase == ArenaPhase.RewardPending) return View();
        if (profile.Pending != null) return Error("A match is pending.");
        return Change(next => {
            next.ArenaRun.End(true, LocalProfile.RewardPackSet(next, new LocalMatchRecord { Id = run.RunId, Seed = unchecked((int)run.Seed) }));
            LocalProfile.PayArenaReward(next);
        });
    }

    public ArenaView AcknowledgeReward(string runId)
    {
        var run = profile.ArenaRun;
        if (run == null && profile.ArenaReceiptSeen(runId)) return View();
        if (run?.Phase != ArenaPhase.RewardPending || run.RunId != runId) return Error("No matching Arena reward.");
        return Change(next => next.ArenaRun = null);
    }

    public bool StartMatch(out string error) { error = "not implemented"; return false; }
}

public sealed partial class LocalProfile
{
    internal const int MaximumArenaRequests = MaximumRecords;
    public ArenaRun ArenaRun => data.ArenaRun?.Copy();
    internal bool ArenaRequestSeen(string id) => data.ArenaEntryRequestIds.Contains(id);
    internal bool ArenaReceiptSeen(string id) => data.ArenaReceipts.Any(row => row.RunId == id);

    // Follow-up match integration calls this inside its existing CommitChange, alongside per-match XP.
    public static void SettleArenaMatch(LocalProfileData next, string result)
    {
        if (next.ArenaRun == null) throw new InvalidOperationException("No Arena run.");
        var run = next.ArenaRun;
        string pack = RewardPackSet(next, new LocalMatchRecord { Id = run.RunId, Seed = unchecked((int)run.Seed) });
        next.ArenaRun = run.SettleMatch(result, pack);
        if (next.ArenaRun.Phase == ArenaPhase.RewardPending) PayArenaReward(next);
    }

    internal static void PayArenaReward(LocalProfileData next)
    {
        var run = next.ArenaRun;
        if (run?.Phase != ArenaPhase.RewardPending || run.Reward == null || next.ArenaReceipts.Any(row => row.RunId == run.RunId))
            throw new InvalidOperationException("Arena reward already paid or not due.");
        next.GoldBalance = checked(next.GoldBalance + run.Reward.Gold);
        next.DustBalance = checked(next.DustBalance + run.Reward.Dust);
        foreach (string pack in run.Reward.PackTypes) AddPacks(next, pack, 1);
        next.ArenaReceipts.Add(new ArenaReceipt { RunId = run.RunId, EntryRequestId = run.EntryRequestId,
            RulesVersion = run.RulesVersion, GrindTier = run.GrindTier, Wins = run.Wins, Losses = run.Losses,
            Reward = ArenaRun.CopyReward(run.Reward), Retired = run.Retired });
        if (next.ArenaReceipts.Count > 20) next.ArenaReceipts.RemoveAt(0);
    }

    private static void ValidateArena(LocalProfileData next)
    {
        if (next.ArenaReceipts == null || next.ArenaEntryRequestIds == null || next.ArenaReceipts.Count > 20
            || next.ArenaEntryRequestIds.Count > MaximumArenaRequests || next.ArenaEntryRequestIds.Any(id => !ArenaRun.ValidRequest(id))
            || next.ArenaEntryRequestIds.Distinct(StringComparer.Ordinal).Count() != next.ArenaEntryRequestIds.Count
            || next.ArenaReceipts.Any(row => row == null)
            || next.ArenaReceipts.Select(row => row.RunId).Distinct(StringComparer.Ordinal).Count() != next.ArenaReceipts.Count
            || next.ArenaReceipts.Select(row => row.EntryRequestId).Distinct(StringComparer.Ordinal).Count() != next.ArenaReceipts.Count) ArenaRun.Invalid();
        foreach (var row in next.ArenaReceipts) {
            var rules = DraftRules.Arena(row.RulesVersion);
            if (!Guid.TryParseExact(row.RunId, "D", out _) || !next.ArenaEntryRequestIds.Contains(row.EntryRequestId)
                || !GrindTiers.IsKnown(row.GrindTier) || row.Wins < 0 || row.Wins > rules.MaxWins || row.Losses < 0 || row.Losses > rules.MaxLosses
                || row.Wins == rules.MaxWins && row.Losses == rules.MaxLosses
                || row.Retired && (row.Wins == rules.MaxWins || row.Losses == rules.MaxLosses)
                || !row.Retired && row.Wins != rules.MaxWins && row.Losses != rules.MaxLosses) ArenaRun.Invalid();
            ArenaRun.ValidateReward(row.Reward, row.RunId, row.Wins, row.GrindTier, row.RulesVersion);
        }
        var run = next.ArenaRun;
        if (run == null) return;
        run.Validate();
        if (!next.ArenaEntryRequestIds.Contains(run.EntryRequestId)) ArenaRun.Invalid();
        var receipt = next.ArenaReceipts.SingleOrDefault(row => row.RunId == run.RunId || row.EntryRequestId == run.EntryRequestId);
        if (run.Phase != ArenaPhase.RewardPending) { if (receipt != null) ArenaRun.Invalid(); }
        else if (receipt == null || receipt.RunId != run.RunId || receipt.EntryRequestId != run.EntryRequestId
            || receipt.Wins != run.Wins || receipt.Losses != run.Losses || receipt.Retired != run.Retired
            || receipt.GrindTier != run.GrindTier || receipt.RulesVersion != run.RulesVersion
            || receipt.Reward.Gold != run.Reward.Gold || receipt.Reward.Dust != run.Reward.Dust
            || !receipt.Reward.PackTypes.SequenceEqual(run.Reward.PackTypes)) ArenaRun.Invalid();
    }
}

using System.Xml.Serialization;
using SabberStoneCore.Model;
using SabberStoneCore.Enums;

internal static class ArenaChecks
{
    private static readonly XmlSerializer Xml = new XmlSerializer(typeof(LocalProfileData));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Arena: " + message); }
    private static LocalProfileData Read(string path) { using var input = File.OpenRead(path); return (LocalProfileData)Xml.Deserialize(input)!; }
    private static void Write(string path, LocalProfileData data) { using var output = File.Create(path); Xml.Serialize(output, data); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Arena accepted " + message);
    }
    private static void Ok(ArenaView view) => Check(view.Error == null, view.Error ?? "");
    private static void Fund(LocalProfile profile, int gold = 1000) => profile.CommitChange(next => { next.GoldBalance = gold; return true; });
    private static void Draft(ArenaService service)
    {
        Ok(service.Choose(0, 0));
        for (int slot = 1; slot <= 30; slot++) Ok(service.Choose(slot, slot % 3));
        Check(service.View().Phase == ArenaPhase.Ready && service.View().Slot == 30 && service.View().Deck.Count == 30, "30 picks ready");
    }
    private static void Unchanged(LocalProfile profile, string path, Func<ArenaView> action, string message)
    {
        var bytes = File.ReadAllBytes(path); var before = profile.ArenaRun;
        Check(action().Error != null && bytes.SequenceEqual(File.ReadAllBytes(path)), message + " bytes");
        Check(profile.ArenaRun?.Picks.Count == before?.Picks.Count && profile.ArenaRun?.Phase == before?.Phase, message + " memory");
    }
    private static void FailsAtomically(LocalProfile profile, string path, Func<ArenaView> action)
    {
        foreach (string stage in new[] { "write", "flush", "replace" }) {
            profile.CommitFailure = point => { if (point == stage) throw new IOException("Arena injected " + stage); };
            try { Unchanged(profile, path, action, "failed " + stage); }
            finally { profile.CommitFailure = null; }
        }
    }

    internal static void Run(string root)
    {
        Offers(); Rewards();
        string path = Path.Combine(root, "arena-run.xml");
        string runId; List<string> offers;
        using (var profile = new LocalProfile(path)) {
            Fund(profile);
            var service = new ArenaService(profile);
            FailsAtomically(profile, path, () => service.Begin("entry-1"));
            Unchanged(profile, path, () => service.Begin(""), "empty request");
            Ok(service.Begin("entry-1")); runId = service.View().RunId;
            Check(profile.Gold == 850 && service.View().Phase == ArenaPhase.ChoosingHero, "entry cost/phase");
            var bytes = File.ReadAllBytes(path);
            Ok(service.Begin("entry-1")); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "retry entry no write");
            Unchanged(profile, path, () => service.Begin("entry-2"), "second active run");
            Unchanged(profile, path, () => service.Choose(-1, 0), "wrong hero slot");
            Unchanged(profile, path, () => service.Choose(0, 3), "wrong hero index");
            FailsAtomically(profile, path, () => service.Choose(0, 1));
            Ok(service.Choose(0, 1));
            bytes = File.ReadAllBytes(path); Ok(service.Choose(0, 1)); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "hero retry no write");
            Unchanged(profile, path, () => service.Choose(0, 2), "different old hero");
            offers = service.View().Offers;
        }
        // Reopen is the simulated crash after a successful entry/pick commit but before the caller got its reply.
        using (var profile = new LocalProfile(path)) {
            var service = new ArenaService(profile);
            Ok(service.Begin("entry-1")); Check(profile.Gold == 850 && profile.ArenaRun.RunId == runId, "restart entry not charged twice");
            Check(service.View().Offers.SequenceEqual(offers), "restart exact offer");
            FailsAtomically(profile, path, () => service.Choose(1, 2));
            Ok(service.Choose(1, 2));
            var bytes = File.ReadAllBytes(path); Ok(service.Choose(1, 2)); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "card retry no write");
            Unchanged(profile, path, () => service.Choose(1, 1), "different old card");
            Unchanged(profile, path, () => service.Choose(3, 0), "future slot");
            Unchanged(profile, path, () => service.Choose(2, -1), "negative index");
            for (int slot = 2; slot <= 30; slot++) Ok(service.Choose(slot, 0));
            Check(service.View().Phase == ArenaPhase.Ready && service.View().Slot == 30, "draft ready");
            bytes = File.ReadAllBytes(path); Ok(service.Choose(30, 0)); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "last card retry");
            Unchanged(profile, path, () => service.Choose(31, 0), "31st pick");
            Check(!service.StartMatch(out string error) && error == "not implemented", "match stub");
            FailsAtomically(profile, path, service.Retire);
            long dust = profile.DustBalance; int gold = profile.Gold, packs = profile.FeatsSnapshot().UnopenedPacks.Sum(row => row.Count);
            Ok(service.Retire());
            var reward = service.View().Reward;
            Check(reward.Gold == profile.GrindTier.Gold(0) && reward.Dust == profile.GrindTier.Dust(0)
                && reward.PackTypes.Count == 1 && profile.Gold == gold + reward.Gold && profile.DustBalance == dust + reward.Dust
                && profile.FeatsSnapshot().UnopenedPacks.Sum(row => row.Count) == packs + 1, "retire paid atomically");
            bytes = File.ReadAllBytes(path); Ok(service.Retire()); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "retire retry");
            Unchanged(profile, path, () => service.AcknowledgeReward("wrong"), "wrong reward ack");
            FailsAtomically(profile, path, () => service.AcknowledgeReward(runId));
        }
        using (var profile = new LocalProfile(path)) {
            var service = new ArenaService(profile);
            Check(service.View().Phase == ArenaPhase.RewardPending && service.View().Reward.RunId == runId, "reward restart");
            Ok(service.AcknowledgeReward(runId)); Ok(service.AcknowledgeReward(runId));
            Check(service.View().Phase == ArenaPhase.None && profile.FeatsSnapshot().ArenaReceipts.Count == 1, "ack keeps receipt");
            var bytes = File.ReadAllBytes(path); Ok(service.Begin("entry-1")); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "old entry still idempotent");
            Ok(service.Begin("entry-2")); Ok(service.Retire());
            Check(service.View().Reward.Wins == 0, "retire at hero choice");
            Ok(service.AcknowledgeReward(service.View().RunId));
            Ok(service.Begin("entry-3")); Ok(service.Choose(0, 0)); Ok(service.Retire());
            Check(service.View().Reward.Wins == 0, "retire before first card");
            Ok(service.AcknowledgeReward(service.View().RunId));
            for (int i = 0; i < 21; i++) {
                Fund(profile); Ok(service.Begin("rolling-" + i)); Ok(service.Retire());
                Ok(service.AcknowledgeReward(service.View().RunId));
            }
            Check(profile.FeatsSnapshot().ArenaReceipts.Count == 20 && !profile.FeatsSnapshot().ArenaReceipts.Any(row => row.RunId == runId), "audit receipts bounded");
            bytes = File.ReadAllBytes(path); Ok(service.Begin("entry-1")); Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "pruned entry still idempotent");
        }
        Gates(root); Settlement(root); Compatibility(root); Registry(root); Bot();
        Console.WriteLine("PASS Arena: deterministic draft, rune/exclusion rules, restart/retries, atomic rewards, tamper rejection, V15 compatibility, NPC drafter");
    }

    private static void Offers()
    {
        var rules = DraftRules.Arena();
        var activeIds = new HashSet<string>(LocalCardCatalog.ActivePoolIds(), StringComparer.Ordinal);
        foreach (var card in Cards.All.Where(card => SabberStoneCore.Actions.FabledRules.IsFabled(card.Id)
            || SabberStoneCore.Actions.FabledRules.IsCompanion(card.Id) || SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(card.Id)
            || card[(GameTag)2931] > 0 || card[(GameTag)997] > 0 || card[(GameTag)1048] > 0))
            Check(!rules.Eligible(card), "excluded metadata " + card.Id);
        foreach (string id in new[] { "TLC_248", "ETC_080", "TOY_330", "REV_018", "BOT_914", "TOY_700", "DAL_800", "GIL_692", "GIL_826" })
            Check(!rules.Eligible(Cards.All.FirstOrDefault(card => card.Id == id)), "explicit exclusion " + id);
        int runeOffers = 0;
        for (int seed = 0; seed < 200; seed++) {
            var heroes = rules.HeroOffers(seed);
            Check(heroes.Count == 3 && heroes.Distinct().Count() == 3 && heroes.SequenceEqual(rules.HeroOffers(seed)), "hero offers");
            string hero = seed % 2 == 0 ? "HERO_11" : LocalCoreProgression.Heroes[seed % LocalCoreProgression.Heroes.Length];
            var deck = new List<string>();
            for (int slot = 1; slot <= 30; slot++) {
                var offers = rules.Offers(seed, slot, hero, deck);
                Check(offers.Count == 3 && offers.Distinct().Count() == 3 && offers.SequenceEqual(rules.Offers(seed, slot, hero, deck)), "card offers");
                foreach (string id in offers) {
                    var card = Cards.FromId(id);
                    Check(rules.Eligible(card) && activeIds.Contains(id)
                        && LocalDecks.IsAllowedInDeck(card, LocalDecks.HeroClass(hero)), "active class offer " + id);
                    Check((card.Rarity == Rarity.LEGENDARY) == (slot == 1), "legendary slot");
                    if (hero == "HERO_11") {
                        Check(LocalDecks.RuneCounts(deck.Concat(new[] { id }).Select(Cards.FromId)).Sum() <= 3, "DK rune bound");
                        if (LocalDecks.RuneCounts(new[] { card }).Sum() > 0) runeOffers++;
                    }
                }
                // Force rune commitments where possible, including repeated two/three-rune cards.
                deck.Add(hero == "HERO_11" ? offers.OrderByDescending(id => LocalDecks.RuneCounts(new[] { Cards.FromId(id) }).Sum()).First() : offers[seed % 3]);
            }
        }
        Check(runeOffers > 100, "rune test exercised requirements");
    }

    private static void Rewards()
    {
        var rules = DraftRules.Arena();
        foreach (var tier in GrindTiers.Selectable)
            for (int wins = 0; wins <= 12; wins++) {
                var reward = rules.Reward("test", wins, tier.Id, FairPackRules.NewestSet);
                int packs = wins < 3 ? 1 : wins < 7 ? 2 : wins < 10 ? 3 : wins < 12 ? 4 : 5;
                Check(reward.Gold == tier.Gold(75 * wins + (wins == 12 ? 200 : 0))
                    && reward.Dust == tier.Dust(28 * wins) && reward.PackTypes.Count == packs, "reward table " + tier.Id + "/" + wins);
            }
        // No run pays back more than its entry without a win (instant retire must never farm gold, on any pace).
        foreach (var pace in GrindTiers.All) {
            var none = rules.Reward("test", 0, pace.Id, FairPackRules.NewestSet);
            Check(none.Gold + none.Dust / 2.0 + 100 * none.PackTypes.Count < rules.EntryGold, "0-win reward below entry " + pace.Id);
        }
        double expectedWins = 0, games = 0, gold = 0, dust = 0, packValue = 0, probability = 0;
        void End(double p, int wins, int losses) {
            var reward = rules.Reward("test", wins, GrindTiers.Standard.Id, FairPackRules.NewestSet);
            probability += p; expectedWins += p * wins; games += p * (wins + losses);
            gold += p * reward.Gold; dust += p * reward.Dust; packValue += p * reward.PackTypes.Count * 100;
        }
        void Walk(int wins, int losses, double p) {
            if (wins == 12 || losses == 3) { End(p, wins, losses); return; }
            Walk(wins + 1, losses, p / 2); Walk(wins, losses + 1, p / 2);
        }
        Walk(0, 0, 1);
        Check(Math.Abs(probability - 1) < 1e-10 && Math.Abs(expectedWins - 2.99) < .01 && Math.Abs(games - 5.98) < .01
            && gold / games > 36.67 && dust / games > 13.33 && packValue > 150, "50% expectation beats Ranked and packs cover entry");
        Console.WriteLine($"PASS Arena 50% expectation: {expectedWins:F4} wins, {games:F4} games, {gold/games:F4} gold + {dust/games:F4} dust/game, {packValue/100:F4} packs/run (Ranked 36.67g + 13.33d)");
    }

    private static void Gates(string root)
    {
        string path = Path.Combine(root, "arena-gates.xml");
        using var profile = new LocalProfile(path);
        Fund(profile, 149); var service = new ArenaService(profile);
        Unchanged(profile, path, () => service.Begin("poor"), "under 150");
        Fund(profile, 150); Ok(service.Begin("exact")); Check(profile.Gold == 0, "exact 150 entry");
        Ok(service.Retire()); Ok(service.AcknowledgeReward(service.View().RunId)); Fund(profile);
        var match = profile.Begin("mage", "mage", 101);
        Unchanged(profile, path, () => service.Begin("pending"), "pending match");
        profile.Settle(match.Id, "Won", 12, false);
        Unchanged(profile, path, () => service.Begin("reward"), "unacknowledged match reward");
        foreach (var tier in GrindTiers.Selectable) {
            using var paced = new LocalProfile(Path.Combine(root, "arena-pace-" + tier.Id + ".xml"), new ProfileOptions { GrindTier = tier.Id });
            Fund(paced); paced.SetGrindTier(tier.Id);
            var arena = new ArenaService(paced); Ok(arena.Begin("pace"));
            Check(paced.Gold == 850 && paced.ArenaRun.GrindTier == tier.Id, "flat entry and stamped pace " + tier.Id);
            paced.SetGrindTier(tier == GrindTiers.Retail ? GrindTiers.Relaxed6x.Id : GrindTiers.Retail.Id);
            Ok(arena.Retire());
            Check(arena.View().Reward.Gold == tier.Gold(0) && arena.View().Reward.Dust == tier.Dust(0), "retire uses stamped pace " + tier.Id);
        }
    }

    private static void Settlement(string root)
    {
        string path = Path.Combine(root, "arena-settlement.xml");
        using var profile = new LocalProfile(path); Fund(profile, 10000);
        var service = new ArenaService(profile); Ok(service.Begin("settle")); Draft(service);
        var initial = profile.ArenaRun;
        var rules = DraftRules.Arena(); string pack = FairPackRules.NewestSet;
        foreach (string result in new[] { "Lost", "Conceded", "Abandoned" })
            Check(initial.SettleMatch(result, pack).Losses == 1 && initial.Losses == 0, result + " pure loss");
        Check(initial.SettleMatch("Won", pack).Wins == 1 && initial.Wins == 0, "pure win");
        Check(initial.SettleMatch("Tied", pack).Wins == 0 && initial.SettleMatch("Tied", pack).Losses == 0, "tie");
        Reject(() => initial.SettleMatch("bogus", pack), "bad match result");
        initial.Wins = 11;
        var finished = initial.SettleMatch("Won", pack);
        Check(finished.Wins == 12 && finished.Phase == ArenaPhase.RewardPending && finished.Reward.Gold == profile.GrindTier.Gold(1100), "12 win end");
        initial.Wins = 0; initial.Losses = 2; finished = initial.SettleMatch("Abandoned", pack);
        Check(finished.Losses == 3 && finished.Phase == ArenaPhase.RewardPending && finished.Reward.PackTypes.Count == 1, "3 loss end");
        // Profile-side integration API must credit terminal reward and receipt in that same commit.
        profile.CommitChange(next => { next.ArenaRun.Wins = 11; return true; });
        foreach (string stage in new[] { "write", "flush", "replace" }) {
            byte[] before = File.ReadAllBytes(path); int oldGold = profile.Gold;
            profile.CommitFailure = point => { if (point == stage) throw new IOException("Arena settle injected"); };
            try { profile.CommitChange(next => { LocalProfile.SettleArenaMatch(next, "Won"); return true; }); throw new Exception("expected failure"); }
            catch (IOException) { }
            finally { profile.CommitFailure = null; }
            Check(before.SequenceEqual(File.ReadAllBytes(path)) && profile.Gold == oldGold && profile.ArenaRun.Wins == 11, "terminal settlement failure");
        }
        int gold = profile.Gold; long dust = profile.DustBalance;
        profile.CommitChange(next => { LocalProfile.SettleArenaMatch(next, "Won"); return true; });
        Check(profile.ArenaRun.Phase == ArenaPhase.RewardPending && profile.Gold == gold + profile.ArenaRun.Reward.Gold
            && profile.DustBalance == dust + profile.ArenaRun.Reward.Dust && profile.FeatsSnapshot().ArenaReceipts.Count == 1, "terminal settlement atomic credits");
        Reject(() => profile.CommitChange(next => {
            next.ArenaRun.Retired = true; next.ArenaReceipts.Last().Retired = true; return true;
        }), "retired terminal run");
        Reject(() => profile.CommitChange(next => { LocalProfile.SettleArenaMatch(next, "Won"); return true; }), "double terminal settlement");
        Ok(service.AcknowledgeReward(service.View().RunId)); Ok(service.Begin("loss-settle")); Draft(service);
        profile.CommitChange(next => { next.ArenaRun.Losses = 2; return true; });
        profile.CommitChange(next => { LocalProfile.SettleArenaMatch(next, "Lost"); return true; });
        Check(profile.ArenaRun.Losses == 3 && profile.ArenaRun.Reward.Wins == 0 && profile.FeatsSnapshot().ArenaReceipts.Count == 2, "profile 3 losses pays once");
    }

    private static void Compatibility(string root)
    {
        string path = Path.Combine(root, "arena-compat.xml");
        using (var profile = new LocalProfile(path)) Fund(profile);
        var old = File.ReadAllBytes(path);
        Check(!File.ReadAllText(path).Contains("Arena"), "old V15 no optional elements");
        var data = Read(path); Write(path, data); Check(old.SequenceEqual(File.ReadAllBytes(path)), "old XML round trip byte identical");
        using (var profile = new LocalProfile(path)) {
            Check(profile.ArenaRun == null, "old V15 no run");
            profile.CommitChange(next => true);
            Check(old.SequenceEqual(File.ReadAllBytes(path)), "old V15 no-op commit byte identical");
            var service = new ArenaService(profile); Ok(service.Begin("tamper")); Ok(service.Choose(0, 0)); Ok(service.Choose(1, 0));
        }
        var valid = Read(path);
        foreach (Action<LocalProfileData> mutate in new Action<LocalProfileData>[] {
            next => next.ArenaRun.Wins = 13, next => next.ArenaRun.Deck[0] = "TLC_248",
            next => next.ArenaRun.Picks[0].Offers.Reverse(), next => next.ArenaRun.EntryGold = 0,
            next => next.ArenaRun.HeroOffers.Reverse(), next => next.ArenaEntryRequestIds.Clear(),
            next => next.ArenaRun.Phase = ArenaPhase.Ready,
        }) {
            Write(path, valid); var tampered = Read(path); mutate(tampered); Write(path, tampered);
            byte[] bytes = File.ReadAllBytes(path);
            Reject(() => { using var bad = new LocalProfile(path); }, "tampered run");
            Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "tamper rejection leaves file");
        }
    }

    private static void Registry(string root)
    {
        using var profile = new LocalProfile(Path.Combine(root, "arena-registry.xml"));
        CraftingLedgerRegistry.Current = new LocalCraftingLedger(profile);
        Check(ArenaServiceRegistry.Current is ArenaService, "bound save registers Arena");
        CraftingLedgerRegistry.Current = null;
        Check(ArenaServiceRegistry.Current == null, "unbound clears Arena");
    }

    private static void Bot()
    {
        double high = 0, low = 0;
        for (int seed = 0; seed < 100; seed++) {
            var best = DraftBot.Draft(seed, 1); var weaker = DraftBot.Draft(seed, 0);
            var repeat = DraftBot.Draft(seed, 1);
            Check(best.Deck.Count == 30 && weaker.Deck.Count == 30 && best.Deck.SequenceEqual(repeat.Deck)
                && best.Hero == repeat.Hero && best.Quality == repeat.Quality, "valid deterministic bot draft");
            LocalDecks.ValidateRunes(best.Deck.Select(Cards.FromId), LocalDecks.HeroClass(best.Hero));
            high += best.Quality; low += weaker.Quality;
        }
        Check(high > low, "skill 1 has higher mean quality");
        Console.WriteLine($"PASS Arena NPC draft quality: skill 1 {high/100:F4}, skill 0 {low/100:F4} (100 seeds)");
    }
}

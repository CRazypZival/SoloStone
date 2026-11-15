using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Model;

// Arena (#6): data/npc/arena-decks-v1.json (decks drafted by the draft bot), the merged library leaving every ladder and Casual pool
// untouched, and the Arena matchmaker (determinism, record-weighted quality buckets, no repeat within a run, no rigging).
internal static class ArenaSuite
{
    internal static void Run()
    {
        var personalities = PersonalityCatalog.Parse(Program.Data("personalities-v1.json"));
        var notables = NpcNotables.Parse(Program.Data("notables-v1.json"));
        var names = NpcNameTables.Parse(Program.Data("names-v1.json"));
        var cosmetics = NpcCosmeticPool.Parse(Program.Data("cosmetics-v1.json"));
        var skills = SkillCatalog.Parse(Program.Data("skills-v1.json"));
        var ranked = NpcDeckLibrary.Parse(Program.Data("decks-v1.json"));
        var casual = CasualDeckCatalog.Parse(Program.Data("casual-decks-v1.json"));
        var arena = ArenaDeckCatalog.Parse(Program.Data("arena-decks-v1.json"));

        Catalog(arena, ranked, casual);
        var merged = arena.MergeInto(casual.MergeInto(ranked));
        var rankedWorld = NpcWorld.Generate(new NpcWorldCatalogs(ranked, skills, personalities, notables, names, cosmetics));
        var world = NpcWorld.Generate(new NpcWorldCatalogs(merged, skills, personalities, notables, names, cosmetics));
        var casualWorld = NpcWorld.Generate(new NpcWorldCatalogs(casual.MergeInto(ranked), skills, personalities, notables, names, cosmetics));
        Pools(rankedWorld, casualWorld, world, ranked, merged, casual);
        var matchmaker = new ArenaMatchmaker(world, arena, null, SyntheticNpcWorld.StubProfile);
        Queue(world, arena, matchmaker);
        Console.WriteLine("arena suite passed");
    }

    private static void Catalog(ArenaDeckCatalog arena, NpcDeckLibrary ranked, CasualDeckCatalog casual)
    {
        Program.Require(arena.Decks.Count == 200, "Arena deck count: " + arena.Decks.Count);
        Program.Require(arena.Library.ActiveMeta == ranked.ActiveMeta, "Arena decks share the active meta");
        Program.Require(arena.Decks.All(d => d.Admission == "arena" && !d.Admitted && d.Origin == "arena-drafted"), "every Arena deck is admission arena");
        Program.Require(arena.Decks.All(d => ranked.Find(d.Id) == null && casual.Library.Find(d.Id) == null), "Arena ids never collide with ranked or Casual decks");
        Program.Require(arena.Buckets.Count == ArenaDeckCatalog.BucketCount && arena.Buckets.All(b => b.Count == 40), "five equal quality buckets");
        for (int b = 1; b < arena.Buckets.Count; b++)
            Program.Require(arena.Buckets[b].Min(d => arena.Quality[d.Id]) >= arena.Buckets[b - 1].Max(d => arena.Quality[d.Id]), "buckets ordered by quality");
        Program.Require(arena.Decks.Select(d => d.Hero).Distinct().Count() >= 8, "Arena decks cover the classes");
        int built = 0;
        foreach (var entry in arena.Decks)
        {
            var deck = NpcDeckLibrary.ToLocalDeck(entry, entry.Name);
            var cards = deck.Build();
            Program.Require(cards.Count == 30 && cards.All(c => c != null && c.Implemented && c.Collectible), "Arena deck loads in the engine: " + entry.Id);
            Program.Require(LocalDecks.PlayerHeroes.Contains(entry.Hero) && Cards.FromId(entry.Hero) != null, "Arena deck hero valid: " + entry.Id);
            Program.Require(AutoDeckPlan.Derive(entry) != null, "Arena deck derives a plan: " + entry.Id);
            built++;
        }
        Program.Require(built == 200, "all Arena decks checked");
        var bad = new List<string>();
        try { ArenaDeckCatalog.Parse(Program.Data("decks-v1.json")); } catch (System.IO.InvalidDataException) { bad.Add("ranked"); }
        try { ArenaDeckCatalog.Parse(Program.Data("casual-decks-v1.json")); } catch (System.IO.InvalidDataException) { bad.Add("casual"); }
        Program.Require(bad.Count == 2, "a ranked or Casual library is refused as an Arena catalog");
    }

    // The merged library adds only non-admitted decks: generation, deck pools, skills, inference and both queues see the same world.
    private static void Pools(NpcWorld rankedWorld, NpcWorld casualWorld, NpcWorld world, NpcDeckLibrary ranked, NpcDeckLibrary merged, CasualDeckCatalog casual)
    {
        Program.Require(casualWorld.All.Count == world.All.Count, "same NPC count");
        for (int i = 0; i < world.All.Count; i++)
        {
            var a = casualWorld.All[i]; var b = world.All[i];
            Program.Require(a.Id == b.Id && a.DisplayName == b.DisplayName && a.HomeRating == b.HomeRating && a.PersonalityId == b.PersonalityId
                && a.Taste == b.Taste && a.SkillId == b.SkillId && a.DeckIds.SequenceEqual(b.DeckIds) && a.CardBack == b.CardBack, "Arena decks change no NPC: " + a.Id);
        }
        Program.Require(ranked.Population().Select(p => p.Id).SequenceEqual(merged.Population().Select(p => p.Id)), "deck inference population is unchanged");
        Program.Require(merged.Admitted.Select(d => d.Id).SequenceEqual(ranked.Admitted.Select(d => d.Id)), "admitted decks are unchanged");
        Program.Require(world.All.All(n => n.DeckIds.All(id => !id.StartsWith("npc-arena-", StringComparison.Ordinal))), "no NPC owns an Arena deck");
        Program.Require(casual.Decks.All(d => !d.Id.StartsWith("npc-arena-", StringComparison.Ordinal)), "Casual catalog holds no Arena deck");
        // Neither queue ever serves an Arena deck.
        var ladder = new LadderMatchmaker(world, null, SyntheticNpcWorld.StubProfile);
        var casualQueue = new CasualMatchmaker(world, casual, null, null, SyntheticNpcWorld.StubProfile);
        int served = 0;
        for (int i = 0; i < 300; i++)
        {
            double rating = 800 + i * 5;
            var l = ladder.Next(new MatchRequest(77 + i, i, Math.Min(150, i / 2), rating, null, new PastEncounter[0]));
            var c = casualQueue.Next(new CasualRequest(78 + i, i, 0, rating, new PastEncounter[0]));
            Program.Require(!l.Deck.Id.StartsWith("npc-arena-", StringComparison.Ordinal) && !c.Deck.Id.StartsWith("npc-arena-", StringComparison.Ordinal), "queues never serve an Arena deck");
            served++;
        }
        Program.Require(served == 300, "queues sampled");
    }

    private static void Queue(NpcWorld world, ArenaDeckCatalog arena, ArenaMatchmaker matchmaker)
    {
        var request = new ArenaRequest(123456789012345L, 3, 2, 1, new[] { "npc-arena-0007" }, new[] { "npc.g.0001" });
        var a = matchmaker.Choose(request); var b = new ArenaMatchmaker(world, arena, null, SyntheticNpcWorld.StubProfile).Choose(request);
        Program.Require(a.Npc.Id == b.Npc.Id && a.Deck.Id == b.Deck.Id && a.MatchSeed == b.MatchSeed && a.Reason == "arena" && a.Deck.Admission == "arena",
            "same inputs, same opponent");
        Program.Require(a.Deck.Id != "npc-arena-0007" && a.Npc.Id != "npc.g.0001", "a used deck or NPC is not met again");
        // The pick depends on the run and the match index, not on anything else.
        Program.Require(Enumerable.Range(0, 20).Select(i => matchmaker.Choose(new ArenaRequest(1000 + i, 0, 0, 0, null, null)).Deck.Id).Distinct().Count() > 10, "runs differ");

        // Bucket weights follow the record: more wins weigh the high-quality buckets, losses the low ones.
        var even = matchmaker.BucketWeights(0, 0); var hot = matchmaker.BucketWeights(9, 0); var cold = matchmaker.BucketWeights(0, 2);
        Program.Require(even.Select((w, i) => new { w, i }).OrderByDescending(x => x.w).First().i == 2, "0-0 favours the middle bucket");
        Program.Require(hot.Select((w, i) => new { w, i }).OrderByDescending(x => x.w).First().i == 4 && cold.Select((w, i) => new { w, i }).OrderByDescending(x => x.w).First().i == 1,
            "wins favour the top bucket, losses the lower one");
        Program.Require(hot[4] / hot.Sum() > even[4] / even.Sum() && cold[0] / cold.Sum() > even[0] / even.Sum(), "weights move with the record");
        double MeanBucket(int wins, int losses)
        {
            double sum = 0; int n = 600;
            for (int i = 0; i < n; i++)
                sum += arena.BucketOf(matchmaker.Choose(new ArenaRequest(5000 + i, wins + losses, wins, losses, null, null)).Deck.Id);
            return sum / n;
        }
        double low = MeanBucket(0, 2), mid = MeanBucket(0, 0), high = MeanBucket(10, 0);
        Program.Require(low < mid && mid < high && high - low > 1, "drawn decks get stronger with the record: " + low.ToString("F2") + " < " + mid.ToString("F2") + " < " + high.ToString("F2"));

        // No repeat within a run (the caller passes the run's earlier opponents), whatever the record path.
        for (int run = 0; run < 40; run++)
        {
            var decks = new List<string>(); var npcs = new List<string>(); int wins = 0, losses = 0;
            for (int m = 0; m < 14; m++)
            {
                var e = matchmaker.Choose(new ArenaRequest(9000 + run, m, wins, losses, decks, npcs));
                Program.Require(!decks.Contains(e.Deck.Id) && !npcs.Contains(e.Npc.Id), "no repeat in a run");
                decks.Add(e.Deck.Id); npcs.Add(e.Npc.Id);
                if ((run + m) % 3 == 0) losses = Math.Min(2, losses + 1); else wins = Math.Min(11, wins + 1);
            }
        }
        // No rigging: the choice reads only the run's seed, index, record and earlier opponents. (The request has no deck, hand or
        // collection field to read; the result of the match being chosen never exists yet.)
        var props = typeof(ArenaRequest).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Program.Require(string.Join(",", props) == "Losses,MatchIndex,RunSeed,UsedDeckIds,UsedNpcIds,Wins", "Arena request carries no player deck state");
        // NPC skill is today's default scale: the NPC's own tier, no Arena difficulty levels.
        var picked = matchmaker.Choose(request);
        var expectedSkill = picked.Npc.SkillId != null ? world.Catalogs.Skills.Find(picked.Npc.SkillId) : NpcPopulationRules.SkillFor(world.Catalogs.Skills, picked.Npc.HomeRating);
        Program.Require(picked.Skill.Id == expectedSkill.Id, "the Arena NPC plays at its own skill tier");
    }
}

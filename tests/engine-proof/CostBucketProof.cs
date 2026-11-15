using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using static Core2026Proof;

/// <summary>#141 interim: the frozen legacy Cost/N minion buckets are regrouped by each card's CURRENT cost (Cards.cs), so a Wild
/// identity row that changes a cost (Mechwarper 2 to 4) never makes a random-by-cost effect return a card of another cost. Standard and
/// Wild format buckets both. Replaced by the format-aware accessor of #141.</summary>
internal static class CostBucketProof
{
    internal static void Run()
    {
        foreach (var format in new[] { FormatType.FT_STANDARD, FormatType.FT_WILD })
        {
            var buckets = Cards.CostMinionCards(format);
            Core2026Proof.Check(buckets[4].Any(c => c.Id == "GVG_006") && buckets[2].All(c => c.Id != "GVG_006"), "Mechwarper (4 Mana) is in Cost/4, not Cost/2: " + format);
            Core2026Proof.Check(buckets[3].Any(c => c.Id == "GVG_109") && buckets[4].All(c => c.Id != "GVG_109"), "Mini-Mage (3 Mana) moved to Cost/3: " + format);
            foreach (var (cost, list) in buckets)
                Core2026Proof.Check(list.All(c => c.Cost == cost), "every Cost/" + cost + " card currently costs " + cost + ": " + format
                    + " " + string.Join(",", list.Where(c => c.Cost != cost).Select(c => c.Id)));
            Core2026Proof.Check(buckets.Values.SelectMany(l => l).GroupBy(c => c.Id).All(g => g.Count() == 1), "no card sits in two buckets: " + format);
        }
        // Silvermoon Portal (SummonRandomMinion(COST, 2)) in a Standard game: only 2-cost minions, over many seeds.
        int seen = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            var game = new Game(new GameConfig { RandomSeed = seed, StartPlayer = 1, Shuffle = false, SkipMulligan = true,
                Player1HeroClass = CardClass.ROGUE, Player2HeroClass = CardClass.MAGE, History = true, Logging = false,
                Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(),
                Player2Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(), FormatType = FormatType.FT_STANDARD });
            game.StartGame();
            var p = game.Player1;
            foreach (var opening in p.HandZone.ToArray()) { p.HandZone.Remove(opening); p.SetasideZone.Add(opening); }
            p.BaseMana = 10;
            var target = Summon(p, "CS2_182");
            Play(game, p, "KAR_077", target);
            var summoned = p.BoardZone.Where(m => m.Id != target.Id).ToArray();
            Core2026Proof.Check(summoned.Length == 1 && summoned[0].Card.Cost == 2 && summoned[0].Card.Type == CardType.MINION,
                "Silvermoon Portal summons one 2-cost minion: " + string.Join(",", summoned.Select(m => m.Card.Id + ":" + m.Card.Cost)));
            seen++;
        }
        Console.WriteLine($"PASS cost buckets regrouped by current Cost (Standard and Wild); Silvermoon Portal {seen} seeds only 2-cost minions (#141)");
    }
}

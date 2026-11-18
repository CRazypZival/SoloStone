using SabberStoneCore.Actions;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using static Core2026Proof;

// Owner rulings of 2026-11-18:
//  - Twig of the World Tree (LOOT_392) follows current retail: "Refresh your Mana Crystals." It refills spent crystals and gains none.
//    The Refresh semantics are pinned (wiki Mana: a refresh refills the controller's non-Overloaded crystals).
//  - #179 Tricksy Improviser never picks a Secret already active for its player, nor one it already picked. Unpinned: a sensible default.
//  - #181 a Zombeast built with a Dormant part (Cyborg Patriarch) is Dormant, on the Build-A-Beast and the Storm the Gates paths. Unpinned.
internal static class Unpinned_TwigSecretsDormantProof
{
    private static void Verify(bool condition, string message) => Core2026Proof.Check(condition, message);
    [GroupProof("twig-secrets-dormant")]
    internal static void Run()
    {
        var failures = new List<string>();
        foreach (var test in new Action[] { TwigRefreshesAndGainsNone, TwigKeepsOverload, TwigText, TricksySkipsActiveAndPicked, TricksyDoesNotDisturbRealPool,
            ZombeastDormantBothPaths, ZombeastWithoutDormantPartIsAwake })
            try { test(); } catch (Exception e) { failures.Add(test.Method.Name + ": " + e.Message); }
        Verify(failures.Count == 0, string.Join("; ", failures));
        Console.WriteLine("PASS Twig refresh; Tricksy Improviser skips active and picked Secrets; Zombeast with a Dormant part is Dormant; Unpinned_TricksyNoDuplicates Unpinned_ZombeastDormant");
    }

    private static void TwigText()
    {
        Verify(Cards.FromId("LOOT_392").Text == "<b>Deathrattle:</b> Refresh your Mana Crystals.", "Twig text is the current retail text, got " + Cards.FromId("LOOT_392").Text);
    }

    private static (Game, Controller, Weapon) EquipTwig(int baseMana, int used)
    {
        var g = DeathKnightC1Proof.NewGame(5); var p = g.Player1;
        var twig = (Weapon)Add(p, "LOOT_392");
        PlayEntity(g, p, twig);
        Verify(p.Hero.Weapon == twig, "Twig equipped");
        p.BaseMana = baseMana; p.UsedMana = used; p.TemporaryMana = 0;
        return (g, p, twig);
    }

    private static void TwigRefreshesAndGainsNone()
    {
        foreach (int max in new[] { 6, 10 })
        {
            var (g, p, twig) = EquipTwig(max, max - 1);
            twig.ToBeDestroyed = true; g.DeathProcessingAndAuraUpdate();
            Verify(p.BaseMana == max && p.UsedMana == 0 && p.RemainingMana == max, $"Twig at {max} crystals refreshes and gains none, got {p.RemainingMana}/{p.BaseMana}");
        }
    }

    private static void TwigKeepsOverload()
    {
        var (g, p, twig) = EquipTwig(6, 6);
        p.OverloadLocked = 2;
        twig.ToBeDestroyed = true; g.DeathProcessingAndAuraUpdate();
        Verify(p.BaseMana == 6 && p.OverloadLocked == 2 && p.RemainingMana == 4, $"Twig refresh leaves Overloaded crystals locked, got {p.RemainingMana}/{p.BaseMana} locked {p.OverloadLocked}");
    }

    // Three real Mage Secrets from the catalog's Tricksy pool.
    internal static string[] PoolOf(Game g, string id) => OfflineCardPools.Candidates(g, g.Player1.HeroClass, LostCityPoolProof.Filter(id), Cards.FromId(id).AssetId)
        .Select(c => c.Id).Take(3).ToArray();

    // The same Standard fixture StandardSetsProof.Case builds (that helper allows one proof per card; Tricksy already has one).
    private static Game StandardGame(int seed)
    {
        var game = new Game(new GameConfig { RandomSeed = seed, StartPlayer = 1, Shuffle = false, SkipMulligan = true,
            Player1HeroClass = CardClass.ROGUE, Player2HeroClass = CardClass.MAGE, History = true, Logging = false,
            Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(),
            Player2Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(), FormatType = FormatType.FT_STANDARD });
        game.StartGame();
        foreach (var owner in new[] { game.Player1, game.Player2 })
        {
            foreach (var opening in owner.HandZone.ToArray()) { owner.HandZone.Remove(opening); owner.SetasideZone.Add(opening); }
            owner.BaseMana = 10;
        }
        return game;
    }

    private static void TricksySkipsActiveAndPicked() { var g = StandardGame(1); const string id = "JAIL_321";
        var Pool = PoolOf(g, id); Verify(Pool.Length == 3, "three catalog Secrets");
        foreach (int seed in Enumerable.Range(1, 12))
        {
            var game = g.CloneExact(history: true); game.SetRandomSeed(seed); var player = game.Player1;
            AutomaticCastProof.Cast(game, (Spell)Entity.FromCard(player, Cards.FromId("GAME_005")));
            LostCityPoolProof.WithPool(game, id, Pool, () => {
                var active = (Spell)Entity.FromCard(player, Cards.FromId(Pool[0])); AutomaticCastProof.Cast(game, active);
                Verify(player.SecretZone.Count == 1, "one active Secret to start with");
                PlayEntity(game, player, Add(player, id));
                var ids = player.SecretZone.Select(s => s.Card.Id).OrderBy(x => x).ToArray();
                Verify(ids.SequenceEqual(Pool.OrderBy(x => x)), $"seed {seed}: Tricksy adds the two other pool Secrets, none twice, got {string.Join(",", ids)}");
                Verify(game.Player1.GraveyardZone.All(c => c.Card.Id != Pool[0] && c.Card.Id != Pool[1] && c.Card.Id != Pool[2]), $"seed {seed}: no duplicate attempt fizzled");
            });
        }
    }

    private static void TricksyDoesNotDisturbRealPool() { var g = StandardGame(1); const string id = "JAIL_321";
        // Real Mage Secret pool, no stand-in: two casts always end as two different Secrets.
        foreach (int seed in Enumerable.Range(1, 30))
        {
            var game = g.CloneExact(history: true); game.SetRandomSeed(seed); var player = game.Player1;
            AutomaticCastProof.Cast(game, (Spell)Entity.FromCard(player, Cards.FromId("GAME_005")));
            PlayEntity(game, player, Add(player, id));
            var ids = player.SecretZone.Select(s => s.Card.Id).ToArray();
            Verify(ids.Length == 2 && ids.Distinct().Count() == 2, $"seed {seed}: two different Secrets from the real pool, got {string.Join(",", ids)}");
        }
    }

    private static void ZombeastDormantBothPaths()
    {
        var patriarch = Cards.FromId("TIME_046"); var raptor = Cards.FromId("CS2_172");
        foreach (bool storm in new[] { false, true })
        foreach (bool patriarchFirst in new[] { false, true })
        {
            var made = Card.CreateZombeastCard(patriarchFirst ? patriarch : raptor, patriarchFirst ? raptor : patriarch, true, storm);
            Verify(made.Dormant != null && made.Dormant.Turns == 3, $"storm={storm} patriarchFirst={patriarchFirst}: Zombeast carries the Dormant definition");
            var g = HistoryFactsProof.Fixture(); var p = g.Player1;
            var minion = (Minion)Entity.FromCard(p, made, zone: p.HandZone);
            PlayEntity(g, p, minion);
            var onBoard = p.BoardZone.OfType<Minion>().Single();
            Verify(onBoard.IsDormant && DormantRules.TurnsRemaining(onBoard) == 3, $"storm={storm} patriarchFirst={patriarchFirst}: played Zombeast is Dormant for 3 turns");
        }
    }

    private static void ZombeastWithoutDormantPartIsAwake()
    {
        foreach (bool storm in new[] { false, true })
        {
            var made = Card.CreateZombeastCard(Cards.FromId("CS2_172"), Cards.FromId("CS2_120"), true, storm);
            Verify(made.Dormant == null, $"storm={storm}: two plain Beasts make an awake Zombeast");
        }
    }
}

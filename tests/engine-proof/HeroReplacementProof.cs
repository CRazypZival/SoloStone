using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using static Core2026Proof;

internal static class HeroReplacementProof
{
    [GroupProof("hero-replacement")]
    internal static void Run()
    {
        foreach (string scenario in new[] { "Jaraxxus", "Majordomo killed", "Majordomo played", "Majordomo silenced" })
        {
            var game = new Game(new GameConfig { RandomSeed = 3, StartPlayer = 1, Shuffle = false, SkipMulligan = true,
                Player1HeroClass = CardClass.PALADIN, Player2HeroClass = CardClass.MAGE, History = true, Logging = false,
                Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(),
                Player2Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(), FormatType = FormatType.FT_WILD });
            game.StartGame();
            var owner = game.Player1;
            foreach (var opening in owner.HandZone.ToArray()) owner.SetasideZone.Add(owner.HandZone.Remove(opening));
            owner.BaseMana = 10;
            Summon(owner, "AT_003"); // Fallen Hero's HERO aura must move with the hero.
            var oldHero = owner.Hero;
            Core2026Proof.Check(oldHero[GameTag.HEROPOWER_DAMAGE] == 1, "fixture: aura on old hero");
            Minion source;
            if (scenario == "Jaraxxus") source = (Minion)Play(game, owner, "EX1_323");
            else if (scenario == "Majordomo played") source = (Minion)Play(game, owner, "BRM_027");
            else
            {
                source = Summon(owner, "BRM_027");
                if (scenario == "Majordomo silenced") source.Silence();
                // A real lethal spell runs the Deathrattle through normal resolution.
                Play(game, owner, "EX1_279", source);
            }
            game.DeathProcessingAndAuraUpdate();
            bool replaced = scenario == "Jaraxxus" || scenario == "Majordomo killed";
            var hero = owner.Hero;
            Core2026Proof.Check((hero.Id != oldHero.Id) == replaced, scenario + " replacement timing");
            if (replaced)
            {
                Core2026Proof.Check(hero.Card.Id == (scenario == "Jaraxxus" ? "EX1_323h" : "BRM_027h"), scenario + " replacement card");
                Core2026Proof.Check(hero[GameTag.CONTROLLER] == owner.PlayerId && hero[GameTag.ENTITY_ID] == hero.Id, scenario + " live identity");
                var full = game.PowerHistory.Full.OfType<PowerHistoryFullEntity>().Last(row => row.Entity.Id == hero.Id);
                Core2026Proof.Check(full.Entity.Tags.TryGetValue(GameTag.CONTROLLER, out int controller) && controller == owner.PlayerId
                    && full.Entity.Tags.TryGetValue(GameTag.ENTITY_ID, out int id) && id == hero.Id, scenario + " FULL identity");
                Core2026Proof.Check(hero[GameTag.HEROPOWER_DAMAGE] == 1, scenario + " aura rebound to new hero");
            }
            if (scenario.StartsWith("Majordomo"))
                Core2026Proof.Check(source.Zone.Type == (scenario == "Majordomo played" ? Zone.PLAY : Zone.GRAVEYARD), scenario + " source stays in its zone");
            Console.WriteLine("PASS hero replacement engine: " + scenario);
        }
    }
}

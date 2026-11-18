extern alias TransportFixtures;
using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

internal static partial class Program
{
    // #80: the native client marks an opposing card burned from deck to graveyard as "being drawn by the opponent" for good and
    // waits on it whenever the card is moved, shown or reset again, so the GAME_RESET never ran and input stayed off. A Rewind
    // after an opposing burn must leave that card in GRAVEYARD.
    private static IReadOnlyList<string> RewindMillFaults(Capture capture) =>
        TransportFixtures::TransportInvariants.BoardActorInvariant.Validate(capture.HistorySnapshots());

    private static int BurnedCards(Capture capture, int cursor = 0) =>
        capture.HistorySnapshots(cursor).Sum(h => h.List.Count(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.BURNED_CARD));

    private static void RewindAfterOpposingBurn()
    {
        foreach (string source in new[] { "TIME_000", "TIME_003", "TIME_034", "END_000p" })
        {
            string name = "rewind after opposing burn " + source;
            var capture = new Capture();
            bool power = source == "END_000p";
            var scenario = DebugScenario.Parse("humanSeat=first\noverdraw=npc-godfrey\nhumanMana=10" + (power ? "" : "\nhumanHand=" + source));
            var match = new NativeMatch(capture.Send, "mage", "mage", 8427) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer };
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game;
            // Turn 1 ends; on turn 2 the NPC draws into a full hand and Godfrey's Atlas burns the overflow to its graveyard.
            var end = capture.Last<AllOptions>(14);
            match.SendOption(end.Id, end.Options.FindIndex(o => o.Type_ == PegasusGame.Option.Type.END_TURN), 0, -1, 0);
            Check(BurnedCards(capture) > 0, name + ": the NPC burned a card before the Rewind");
            Check(game.CurrentPlayer == game.Player1, name + ": the human is on turn");
            var options = capture.Last<AllOptions>(14);
            int cursor = capture.WirePackets.Count;
            int play;
            if (power)
            {
                game.Player1.Hero.Card = Cards.FromId("HERO_03"); // a Rogue: Blessing of the Bronze is the Imbued hero power
                ImbueRules.Imbue(game.Player1, game.Player1.Hero);
                FlushFieldHistory(match);
                typeof(NativeMatch).GetMethod("SendHumanOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(match, null);
                options = capture.Last<AllOptions>(14);
                var hp = game.Player1.Hero.HeroPower;
                Check(hp.Card.Id == "END_000p", name + ": Blessing of the Bronze is the hero power: " + hp.Card.Id);
                play = options.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(hp.Id));
                Check(play >= 0, name + ": hero power option offered");
                match.SendOption(options.Id, play, 0, -1, 0);
            }
            else
            {
                var card = game.Player1.HandZone.First(c => c.Card.Id == source);
                play = options.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(card.Id));
                Check(play >= 0, name + ": play option offered");
                match.SendOption(options.Id, play, 0, -1, card.Card.Type == CardType.MINION ? 1 : 0);
            }
            Check(game.Rewind != null && game.Player1.Choice?.ChoiceAction == ChoiceAction.REWIND, name + ": Rewind offered");
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int> { game.Rewind.RewindEntityId });
            FlushFieldHistory(match);
            Check(game.Rewind == null && game.Player1.Choice == null, name + ": Rewind settled");
            var faults = RewindMillFaults(capture);
            Check(faults.Count == 0, name + ": the burned opposing card never moves again: " + string.Join("; ", faults.Take(3)));
            var after = capture.Last<AllOptions>(14);
            Check(after.Options.Any(o => o.Type_ == PegasusGame.Option.Type.END_TURN), name + ": End Turn offered after Rewind");
            capture.SerializeAll();
        }
        Console.WriteLine("PASS Rewind after an opposing burn: the burned card stays in GRAVEYARD, End Turn offered (#80)");
    }
}

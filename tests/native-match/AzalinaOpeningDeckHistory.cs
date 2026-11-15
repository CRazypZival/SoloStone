using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PegasusGame;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineZone = SabberStoneCore.Enums.Zone;

internal static partial class Program
{
    // Azalina Soulsever adds 20 copies of the opponent's cards to its owner's starting deck before the opening draw. They were
    // created outside any zone and then added to the deck, so each went out as a FULL_ENTITY with its card id and ZONE=INVALID.
    // The client drew a copy dealt into the opening hand face up at the deck, half off the right edge of the screen, during the
    // VS intro (Steam Deck playtest 2026-11-15). Every copy must look like any other starting deck card: an anonymous DECK
    // FULL_ENTITY, revealed only by the SHOW_ENTITY that moves it into the opening hand.
    [GroupProof("azalina-opening-deck", ProofKind.Native)]
    private static void AzalinaOpeningDeckHistory()
    {
        var own = new[] { "JAIL_430" }.Concat(Enumerable.Repeat("CS2_231", 19)).Select(Cards.FromId).ToList();
        var bot = Enumerable.Repeat("CS2_182", 15).Concat(Enumerable.Repeat("CS2_120", 15)).ToArray();
        int handCopies = 0, deckCopies = 0;
        foreach (int seat in new[] { 1, 2 })
        for (int seed = 1; seed <= 6; seed++)
        {
            var capture = new Capture();
            var match = new NativeMatch(capture.Send, Cards.FromId("HERO_09"), own, seed) { StartingPlayer = seat };
            typeof(NativeMatch).GetField("opponentDeck", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(match, new LocalDeck("Azalina fixture opponent", "HERO_04", bot));
            match.Start();
            var game = match.Game;
            int azalina = game.IdEntityDic.Values.Single(e => e.Controller == game.Player1 && e.Card.Id == "JAIL_430").Id;
            var copies = game.IdEntityDic.Values.Where(e => e.Controller == game.Player1 && e.Id != azalina
                && e[GameTag.CREATOR] == azalina).ToArray();
            Check(copies.Length == 20, "Azalina must add 20 copies, got " + copies.Length + " (seed " + seed + ", seat " + seat + ")");
            // Everything sent up to and including the human mulligan choice.
            var rows = capture.Packets.Where(p => p.Id == 19).SelectMany(p => ((PowerHistory)p.Message).List).ToList();
            foreach (var copy in copies)
            {
                string where = copy.Card.Id + "#" + copy.Id + " seed " + seed + " seat " + seat;
                var fulls = rows.Where(r => r.HasFullEntity && r.FullEntity.Entity == copy.Id).ToArray();
                Check(fulls.Length == 1, "copy must be created once: " + where);
                Check(string.IsNullOrEmpty(fulls[0].FullEntity.Name), "copy FULL_ENTITY names its card: " + where);
                Check(TagValue(fulls[0].FullEntity.Tags, GameTag.ZONE) == (int)EngineZone.DECK,
                    "copy FULL_ENTITY is not created in DECK (zone " + TagValue(fulls[0].FullEntity.Tags, GameTag.ZONE) + "): " + where);
                Check(!rows.Any(r => r.HasTagChange && r.TagChange.Entity == copy.Id && r.TagChange.Tag == (int)GameTag.ZONE
                    && r.TagChange.Value == (int)EngineZone.DECK), "copy moves into DECK after creation: " + where);
                var shows = rows.Where(r => r.HasShowEntity && r.ShowEntity.Entity == copy.Id).ToArray();
                if (copy.Zone.Type == EngineZone.HAND)
                {
                    handCopies++;
                    Check(shows.Length == 1 && TagValue(shows[0].ShowEntity.Tags, GameTag.ZONE) == (int)EngineZone.HAND
                        && shows[0].ShowEntity.Name == copy.Card.Id, "opening-hand copy must be revealed once, into HAND: " + where);
                }
                else
                {
                    deckCopies++;
                    Check(copy.Zone.Type == EngineZone.DECK && shows.Length == 0, "deck copy revealed in the opening: " + where);
                }
            }
        }
        Check(handCopies > 0 && deckCopies > 0, "fixture must deal copies into the opening hand and keep others in the deck");
        Console.WriteLine("PASS azalina opening deck: " + handCopies + " opening-hand copies revealed once into HAND, "
            + deckCopies + " deck copies anonymous");
    }
}

using EngineGame = SabberStoneCore.Model.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using EngineZone = SabberStoneCore.Enums.Zone;

internal static partial class Program
{
    private static void Invoke0(NativeMatch match, string name) =>
        typeof(NativeMatch).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(match, null);

    // Compare the complete answer's wire prefix before options (human) or timeout/end-turn (timer).
    private static void TimedChoiceCompletionNative()
    {
        foreach (string finish in new[] { "20s", "35s", "timer", "send-at-deadline", "watchdog" })
        foreach (string cardId in new[] { "TIME_041", "UNG_941", "TIME_034", "CATA_200" })
        {
            string name = cardId + " choice " + finish;
            List<string> expected = null;
            for (int run = 0; run < 2; run++)
            {
                long now = 0;
                var capture = new Capture();
                var scenario = DebugScenario.Parse("humanHand=" + cardId + ",CS2_182\nhumanSeat=first");
                var match = new NativeMatch(capture.Send, "mage", "mage", 8427) {
                    DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer,
                    PlayerTurnTimerEnabled = true, TurnTimerClock = () => now, NativeHumanInputReady = () => true };
                match.Start();
                int mulliganPackets = capture.Packets.Count;
                match.TickTurnTimer(100000);
                Check(match.Game.Step == Step.BEGIN_MULLIGAN && capture.Packets.Count == mulliganPackets, name + ": mulligan has no turn timer");
                match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
                var game = match.Game; var player = game.Player1;
                player.BaseMana = 10; player.UsedMana = 0;
                var source = player.HandZone.Single(c => c.Card.Id == cardId);
                FlushFieldHistory(match); Invoke0(match, "SendHumanOptions");
                match.TickTurnTimer();
                var options = capture.Last<AllOptions>(14);
                int play = options.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(source.Id));
                Check(play >= 0, name + ": playable");
                match.SendOption(options.Id, play, 0, -1, source.Card.Type == CardType.MINION ? 1 : 0);
                var offer = capture.Last<EntityChoices>(17);
                Check(player.Choice != null && offer.CountMin == 1, name + ": mandatory choice published");
                bool automatic = finish == "timer" || finish == "send-at-deadline" || finish == "watchdog";
                int pick = finish == "watchdog" ? offer.Entities[0]
                    : offer.Entities[new Random(match.Seed ^ 0x71a3).Next(offer.Entities.Count)];
                now = run == 0 ? 1000 : finish == "20s" ? 20000 : finish == "35s" ? 35000 : finish == "watchdog" ? 19999 : 44999;
                match.TickTurnTimer();
                Check(player.Choice != null, name + ": choice remains human-owned before expiry");
                int cursor = capture.Packets.Count;
                if (run == 0 || !automatic) match.SendChoices(offer.Id, new List<int> { pick });
                else if (finish == "watchdog")
                {
                    var presentation = MissingChoicePresentation(offer.Id);
                    presentation.Type = player.Choice.ChoiceType;
                    match.NativeHumanChoicePresentation = () => presentation;
                    match.NpcClock = () => now;
                    for (now = 20000; now <= 35000; now += 500) match.TickChoiceWatchdog();
                }
                else
                {
                    now = 45000;
                    match.NativeHumanInputReady = () => false;
                    if (finish == "send-at-deadline")
                        ExpectTimerRejection(() => match.SendChoices(offer.Id, new List<int> { offer.Entities.Last() }), name);
                    else match.TickTurnTimer();
                }
                Check(player.Choice == null && game.Rewind == null, name + ": engine choice settled");
                var answer = capture.Packets.Skip(cursor)
                    .TakeWhile(p => p.Id != 14 && p.Message is not PegasusGame.TurnTimer { Seconds: 0 }).ToArray();
                var chosen = answer.Where(p => p.Id == 13).Select(p => (EntitiesChosen)p.Message).Single();
                Check(chosen.ChooseEntities.Id == offer.Id && chosen.ChooseEntities.Entities.SequenceEqual(new[] { pick }),
                    name + ": exactly one acknowledgement for the chosen offer");
                if (cardId == "TIME_041") AssertGuessResult(capture, cursor, game, offer.Entities, pick, false, name);
                var wire = capture.WirePackets.Skip(cursor).Take(answer.Length).Where(p => p.Id != 9).Select(p => {
                    // Choice packet ids are global across matches; every other byte must agree.
                    if (p.Id == 13) return "13:" + chosen.PlayerId + ":" + chosen.ChoiceType + ":" + string.Join(",", chosen.ChooseEntities.Entities);
                    return p.Id + ":" + Convert.ToBase64String(p.Bytes);
                }).ToList();
                if (run == 0) expected = wire;
                else
                {
                    Check(expected.SequenceEqual(wire), name + ": answer wire matches human pick, including history and actor retirement");
                    Console.WriteLine("PASS " + name + ": answer wire matches human pick");
                    if (automatic)
                    {
                        int packets = capture.Packets.Count;
                        // Do not let a new turn's clock obscure whether the stale send changed anything.
                        match.NativeHumanTimerReady = () => false;
                        ExpectTimerRejection(() => match.SendChoices(offer.Id, new List<int> { offer.Entities.Last() }), name + " late pick");
                        Check(capture.Packets.Skip(packets).All(p => p.Id == 9), name + ": late pick emits no duplicate completion");
                    }
                }
                capture.SerializeAll();
            }
        }
        Console.WriteLine("PASS timed choices: Forefather, Discover, Rewind, TARGET; delayed input, expiry, watchdog, late rejection; mulligan untimed");
    }

    // #115: after Futuristic Forefather's guess the choice stayed on screen and the match stopped. The native choice path
    // from the human's seat: play, answer each offer, then the match must offer options and the turn must pass.
    private static void ForefatherGuessNative()
    {
        foreach (string premium in new[] { "-", "golden", "diamond", "signature" })
        foreach (int pick in new[] { 0, 1, 2 })
        foreach (bool secondCard in new[] { false, true })
        foreach (bool discovered in new[] { false, true })
        foreach (bool emptyEnemyHand in new[] { false, true })
        {
            string name = $"Forefather guess (premium {premium}, pick {pick}, full hand {secondCard}, discovered {discovered}, empty enemy hand {emptyEnemyHand})";
            var capture = new Capture();
            // The report's screenshot shows a crowded hand: the second variant plays from five cards (the scenario maximum).
            string hand = secondCard ? "TIME_041" + string.Concat(Enumerable.Repeat(",CS2_182", 4)) : "TIME_041";
            string mods = premium == "-" ? "" : "\nhumanHandMods=TIME_041,-,-,-," + premium;
            var scenario = DebugScenario.Parse("humanHand=" + hand + mods + "\nhumanSeat=first");
            var log = new List<string>();
            var match = new NativeMatch(capture.Send, "mage", "mage", 8427) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer, BotTrace = log.Add };
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game; var player = game.Player1;
            player.BaseMana = 10; player.UsedMana = 0;
            var source = player.HandZone.Single(c => c.Card.Id == "TIME_041");
            // A Discover's pick enters the hand as the offer actor itself: it keeps the private-offer tag and its creator.
            if (discovered) { source[Generic.PrivateDecisionOfferTag] = player.PlayerId; source[GameTag.CREATOR] = player.Hero.Id; source[GameTag.DISPLAYED_CREATOR] = player.Hero.Id; }
            if (emptyEnemyHand) foreach (var held in game.Player2.HandZone.ToArray()) game.Player2.SetasideZone.Add(game.Player2.HandZone.Remove(held));
            FlushFieldHistory(match);
            Invoke0(match, "SendHumanOptions");
            var before = capture.Last<AllOptions>(14);
            int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(source.Id) && o.MainOption.PowerKeyword == 0);
            Check(play >= 0, name + ": native play option");
            match.SendOption(before.Id, play, 0, -1, 1);
            Check(player.Choice != null, name + ": guess offered");
            var offer = capture.Last<EntityChoices>(17);
            Check(offer.Entities.Count == 3, name + ": three offers");
            int cursor = capture.Packets.Count;
            // Nothing before the pick may name the correct offer (it would give the guess away).
            Check(!GuessTargets(capture, 0).Any(), name + ": no guess result on the wire before the pick");
            match.SendChoices(offer.Id, new List<int> { offer.Entities[pick] });
            AssertGuessResult(capture, cursor, game, offer.Entities, offer.Entities[pick], emptyEnemyHand, name);
            FlushFieldHistory(match);
            Check(player.Choice == null, name + ": engine choice settled");
            var options = capture.Last<AllOptions>(14);
            Check(options.Id != before.Id && options.Options.Any(o => o.Type_ == PegasusGame.Option.Type.END_TURN), name + ": End Turn offered after the guess");
            if (secondCard)
                Check(options.Options.Any(o => o.MainOption != null && o.MainOption.Id != 0 && game.IdEntityDic.TryGetValue(match.EngineReturnId(o.MainOption.Id), out var e) && e.Card.Id == "CS2_182"),
                    name + ": the other hand card is still playable");
            int turn = game.Turn;
            int end = options.Options.FindIndex(o => o.Type_ == PegasusGame.Option.Type.END_TURN);
            match.SendOption(options.Id, end, 0, -1, 0);
            Check(game.Turn >= turn + 2 || game.State == State.COMPLETE, name + ": the opponent's turn passes back (turn " + turn + " -> " + game.Turn + ")");
            Check(match.BotFallbacks == 0 && !log.Any(l => l.Contains("fallback=") || l.Contains("rror") || l.Contains("fault")),
                name + ": the opponent searched without a fallback: fallbacks=" + match.BotFallbacks + " " + string.Join(" | ", log.Where(l => l.Contains("fallback=") || l.Contains("rror")).Take(3)));
            capture.SerializeAll();
        }
        Console.WriteLine("PASS Futuristic Forefather guess: native choice path, every premium, every offer, End Turn passes (#115)");
    }

    // #80 with the reporter's deck: the Herald Death Knight recipe (real DK hero, runes, corpses on in every native match),
    // Stadium Announcer at a Health cost (a Herald gift), Rewind chosen, through the native option and choice path.
    private static void AnnouncerRewindDeathKnightNative()
    {
        var recipe = LocalShop.Default.RecipeRef("herald-death-knight");
        var ids = recipe.Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count)).ToArray();
        foreach (string payment in new[] { "humanHandHealth", "humanHandHealthWing" })
        foreach (int seed in new[] { 8427, 8428, 8429 })
        foreach (bool keepFirst in new[] { false, true })
        foreach (bool dk in new[] { false, true })
        {
            string name = $"{(dk ? "DK" : "Mage")} Announcer Rewind ({payment}, seed {seed}, keep-then-play {keepFirst})";
            Console.WriteLine("RUN " + name);
            var capture = new Capture();
            var scenario = DebugScenario.Parse("humanHand=TIME_034\n" + payment + "=TIME_034\nhumanSeat=first");
            var deck = new LocalDeck("Herald DK", recipe.Hero, ids);
            var match = (dk ? new NativeMatch(capture.Send, deck, "mage", seed) : new NativeMatch(capture.Send, "mage", "mage", seed));
            match.DebugScenario = scenario; match.StartingPlayer = scenario.FirstPlayer;
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game; var player = game.Player1;
            Check(game.EnableCorpses && (player.Hero.Card.Class == CardClass.DEATHKNIGHT) == dk, name + ": a corpse-enabled match of the expected class");
            player.BaseMana = 10; player.UsedMana = 0;
            var announcer = (Playable)player.HandZone.Single(c => c.Card.Id == "TIME_034");
            FlushFieldHistory(match);
            int lastOptions = -1;
            for (int attempt = 0; attempt < (keepFirst ? 2 : 1); attempt++)
            {
                Invoke0(match, "SendHumanOptions");
                var before = capture.Last<AllOptions>(14);
                int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(announcer.Id) && o.MainOption.PowerKeyword == 0);
                Check(play >= 0, name + ": native play option");
                lastOptions = before.Id;
                match.SendOption(before.Id, play, 0, -1, 1);
                Check(game.Rewind != null && player.Choice?.ChoiceAction == ChoiceAction.REWIND, name + ": Rewind offered");
                var offer = capture.Last<EntityChoices>(17);
                bool rewind = attempt == (keepFirst ? 1 : 0);
                // No extra history flush here: the match has already sent the reset block, and a second send would replay it.
                match.SendChoices(offer.Id, new List<int> { rewind ? game.Rewind.RewindEntityId : game.Rewind.KeepEntityId });
                if (!rewind) break;
            }
            Check(game.Rewind == null && player.Choice == null, name + ": Rewind settled");
            var options = capture.Last<AllOptions>(14);
            Check(options.Options.Any(o => o.Type_ == PegasusGame.Option.Type.END_TURN), name + ": End Turn offered after Rewind");
            Check(options.Id != lastOptions, name + ": the match published fresh options after the Rewind");
            int turn = game.Turn;
            var humanOptionsField = typeof(NativeMatch).GetField("humanOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var held = humanOptionsField.GetValue(match) as SabberStoneCore.Kettle.PowerAllOptions;
            Console.WriteLine($"  last options id={options.Id}, registered={(held == null ? "none" : held.Index.ToString())}, current={game.CurrentPlayer.Name} step={game.Step} turn={game.Turn} packets after={capture.Packets.Count}");
            Check(held != null && held.Index == options.Id, name + ": the fresh options are the ones the match accepts (registered " + (held == null ? "none" : held.Index.ToString()) + ")");
            match.SendOption(options.Id, options.Options.FindIndex(o => o.Type_ == PegasusGame.Option.Type.END_TURN), 0, -1, 0);
            Check(game.Turn >= turn + 2 || game.State == State.COMPLETE, name + ": the turn passes");
            capture.SerializeAll();
        }
        Console.WriteLine("PASS Stadium Announcer Rewind with the Herald Death Knight deck: corpses on, native path (#80)");
    }

    // #115 by its own route: Futuristic Forefather comes from a Discover (Stonehill Defender), is played, the guess is made, the
    // turn ends. Seeds are searched until the Discover offers the Forefather; golden and Signature Defenders too.
    private static void ForefatherFromDiscoverNative()
    {
        int found = 0;
        foreach (string premium in new[] { "-", "golden", "signature" })
        for (int seed = 1, perPremium = 0; seed <= 400 && perPremium < 4; seed++)
        {
            var capture = new Capture();
            string mods = premium == "-" ? "" : "\nhumanHandMods=Core_UNG_072,-,-,-," + premium;
            var scenario = DebugScenario.Parse("humanHand=Core_UNG_072,CS2_182" + mods + "\nhumanSeat=first");
            var log = new List<string>();
            var match = new NativeMatch(capture.Send, "mage", "mage", seed) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer, BotTrace = log.Add };
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game; var player = game.Player1;
            player.BaseMana = 10; player.UsedMana = 0;
            var defender = player.HandZone.Single(c => c.Card.Id == "Core_UNG_072");
            FlushFieldHistory(match);
            Invoke0(match, "SendHumanOptions");
            var before = capture.Last<AllOptions>(14);
            int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(defender.Id) && o.MainOption.PowerKeyword == 0);
            match.SendOption(before.Id, play, 0, -1, 1);
            if (player.Choice == null) continue;
            var offer = capture.Last<EntityChoices>(17);
            int forefather = offer.Entities.FirstOrDefault(id => game.IdEntityDic[id].Card.Id == "TIME_041");
            if (forefather == 0) continue;
            found++; perPremium++;
            string name = $"Forefather from a Discover (Defender premium {premium}, seed {seed})";
            match.SendChoices(offer.Id, new List<int> { forefather });
            var card = player.HandZone.SingleOrDefault(c => c.Card.Id == "TIME_041");
            Check(card != null, name + ": the Forefather reached the hand");
            player.BaseMana = 10; player.UsedMana = 0;
            Invoke0(match, "SendHumanOptions");
            var options = capture.Last<AllOptions>(14);
            int playForefather = options.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(card.Id) && o.MainOption.PowerKeyword == 0);
            Check(playForefather >= 0, name + ": the Forefather is playable");
            match.SendOption(options.Id, playForefather, 0, -1, 1);
            Check(player.Choice != null, name + ": guess offered");
            var guess = capture.Last<EntityChoices>(17);
            match.SendChoices(guess.Id, new List<int> { guess.Entities[seed % 3] });
            Check(player.Choice == null, name + ": engine choice settled");
            var after = capture.Last<AllOptions>(14);
            var held = typeof(NativeMatch).GetField("humanOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(match) as SabberStoneCore.Kettle.PowerAllOptions;
            Check(held != null && held.Index == after.Id && after.Options.Any(o => o.Type_ == PegasusGame.Option.Type.END_TURN), name + ": options published and accepted after the guess");
            int turn = game.Turn;
            match.SendOption(after.Id, after.Options.FindIndex(o => o.Type_ == PegasusGame.Option.Type.END_TURN), 0, -1, 0);
            Check(game.Turn >= turn + 2 || game.State == State.COMPLETE, name + ": the turn passes back");
            Check(match.BotFallbacks == 0, name + ": the opponent played without a fallback");
            capture.SerializeAll();
        }
        Check(found > 0, "the seed search found a Forefather Discover");
        Console.WriteLine($"PASS Futuristic Forefather from a Discover: {found} routes, play, guess, End Turn (#115)");
    }

    // #115: the guess's conceal spell on the client waits for a META_DATA TARGET naming the correct offer (see NoteGuessResult).
    private static List<int> GuessTargets(Capture capture, int from)
    {
        var found = new List<int>();
        foreach (var row in DeckDiscoverWireHistory(capture, from))
            if (row.HasMetaData && row.MetaData.Type == HistoryMeta.Type.TARGET && row.MetaData.Info.Count == 1 && row.MetaData.HasData
                && capture.Packets.Count > 0) found.Add(row.MetaData.Info[0]);
        return found;
    }

    private static void AssertGuessResult(Capture capture, int from, EngineGame game, IList<int> offers, int chosen, bool noCorrectOffer, string name)
    {
        var rows = DeckDiscoverWireHistory(capture, from);
        int chosenAt = capture.Packets.FindIndex(from, p => p.Id == 13);
        Check(chosenAt >= 0, name + ": EntitiesChosen sent");
        var targets = rows.Where(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.TARGET && r.MetaData.Info.Count == 1 && offers.Contains(r.MetaData.Info[0])).ToList();
        Check(targets.Count == 1, name + ": exactly one META_DATA TARGET names an offer after the pick, found " + targets.Count);
        int named = targets[0].MetaData.Info[0];
        int correct = offers.FirstOrDefault(id => game.IdEntityDic[id][SabberStoneCore.Actions.DiscoverRealRules.CorrectAnswer] == 1);
        if (noCorrectOffer) Check(correct == 0 && named != chosen, name + ": with no correct offer the row names an unchosen offer");
        else Check(correct != 0 && named == correct, name + ": the row names the correct offer " + correct + ", not " + named);
        bool right = named == chosen;
        var forefather = game.IdEntityDic[targets[0].MetaData.Data];
        Check(forefather.Card.Id == "TIME_041", name + ": the row's source is the Forefather");
        int health = ((SabberStoneCore.Model.Entities.Minion)forefather).Health;
        Check(right == (health == 8), name + ": right guess " + right + " matches the Forefather's Health " + health);
        // The row must come after EntitiesChosen (packet order) and live inside the resumed Battlecry block.
        int metaPacket = capture.Packets.FindIndex(from, p => p.Id == 19 && ((PowerHistory)p.Message).List.Any(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.TARGET && r.MetaData.Info.Contains(named)));
        Check(metaPacket > chosenAt, name + ": the row follows EntitiesChosen");
        var list = ((PowerHistory)capture.Packets[metaPacket].Message).List;
        int metaAt = list.FindIndex(r => r.HasMetaData && r.MetaData.Info.Contains(named));
        Check(metaAt > 0 && list[metaAt - 1].HasPowerStart && list[metaAt - 1].PowerStart.Source == forefather.Id, name + ": the row opens the resumed Battlecry block");
    }

    // The same for a Forefather the NPC plays: its guess is shown to the human as card backs and ends with the same conceal spell.
    private static void ForefatherNpcGuessNative()
    {
        int found = 0;
        for (int seed = 1; seed <= 60 && found < 4; seed++)
        {
            var capture = new Capture();
            var scenario = DebugScenario.Parse("npcHand=TIME_041\nnpcMana=10\nhumanSeat=second");
            var match = new NativeMatch(capture.Send, "mage", "mage", seed) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer, BotTrace = _ => { } };
            match.Start();
            var game = match.Game;
            int at = capture.Packets.FindIndex(p => p.Id == 17 && ((EntityChoices)p.Message).PlayerId == 2 && ((EntityChoices)p.Message).ChoiceType == 2);
            try { if (at < 0) match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>()); } catch (Exception) { }
            at = capture.Packets.FindIndex(p => p.Id == 17 && ((EntityChoices)p.Message).PlayerId == 2 && ((EntityChoices)p.Message).ChoiceType == 2);
            if (at < 0) continue;
            var shown = (EntityChoices)capture.Packets[at].Message;
            if (!game.IdEntityDic.TryGetValue(shown.Source, out var src) || src.Card.Id != "TIME_041") continue;
            found++;
            string name = "NPC Forefather guess (seed " + seed + ")";
            Check(!capture.Packets.Take(at + 1).Any(p => p.Id == 19 && ((PowerHistory)p.Message).List.Any(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.TARGET && shown.Entities.Contains(r.MetaData.Info.FirstOrDefault()))),
                name + ": no guess result before the pick");
            int chosenAt = capture.Packets.FindIndex(at, p => p.Id == 13 && ((EntitiesChosen)p.Message).PlayerId == 2);
            Check(chosenAt > at, name + ": the NPC's answer is sent");
            var after = DeckDiscoverWireHistory(capture, chosenAt);
            var targets = after.Where(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.TARGET && r.MetaData.Info.Count == 1 && shown.Entities.Contains(r.MetaData.Info[0])).ToList();
            Check(targets.Count == 1, name + ": one TARGET row names a shown offer after the NPC's pick, found " + targets.Count);
        }
        Check(found > 0, "the NPC played a Forefather in the seed search");
        Console.WriteLine($"PASS NPC Forefather guess: {found} games, the guess result follows the pick only (#115)");
    }

    // Curious Glimmerroot shares the guess's client conceal spell: the same TARGET row, naming the offer that started in the opponent's deck.
    private static void GlimmerrootGuessNative()
    {
        int cases = 0;
        foreach (int pick in new[] { 0, 1, 2 })
        {
            var capture = new Capture();
            var scenario = DebugScenario.Parse("humanHand=UNG_035\nhumanSeat=first");
            var match = new NativeMatch(capture.Send, "mage", "mage", 8427) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer };
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game; var player = game.Player1; player.BaseMana = 10; player.UsedMana = 0;
            var source = player.HandZone.Single(c => c.Card.Id == "UNG_035");
            FlushFieldHistory(match); Invoke0(match, "SendHumanOptions");
            var before = capture.Last<AllOptions>(14);
            int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(source.Id));
            Check(play >= 0, "Glimmerroot playable");
            var option = before.Options[play].MainOption;
            int target = option.Targets.Count > 0 ? option.Targets[0].Id : 0;
            match.SendOption(before.Id, play, target, -1, 1);
            Check(player.Choice != null, "Glimmerroot offers its choice");
            var offer = capture.Last<EntityChoices>(17);
            int cursor = capture.Packets.Count;
            Check(!GuessTargets(capture, 0).Any(), "Glimmerroot: no result before the pick");
            match.SendChoices(offer.Id, new List<int> { offer.Entities[pick] });
            var deck = game.Player2.DeckCards.Select(c => c.Id).ToHashSet();
            var rows = DeckDiscoverWireHistory(capture, cursor);
            var targets = rows.Where(r => r.HasMetaData && r.MetaData.Type == HistoryMeta.Type.TARGET && offer.Entities.Contains(r.MetaData.Info.FirstOrDefault())).ToList();
            Check(targets.Count == 1 && deck.Contains(game.IdEntityDic[targets[0].MetaData.Info[0]].Card.Id), "Glimmerroot: one TARGET row names the offer from the opponent's deck, found " + targets.Count);
            cases++;
        }
        Console.WriteLine($"PASS Curious Glimmerroot: {cases} picks, the result row names the opponent's deck card (#115)");
    }
}

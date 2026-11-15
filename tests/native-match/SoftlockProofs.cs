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

    // #115: after Futuristic Forefather's guess the choice stayed on screen and the match stopped. The native choice path
    // from the human's seat: play, answer each offer, then the match must offer options and the turn must pass.
    private static void ForefatherGuessNative()
    {
        foreach (string premium in new[] { "-", "golden", "diamond", "signature" })
        foreach (int pick in new[] { 0, 1, 2 })
        foreach (bool secondCard in new[] { false, true })
        foreach (bool discovered in new[] { false, true })
        {
            string name = $"Forefather guess (premium {premium}, pick {pick}, full hand {secondCard}, discovered {discovered})";
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
            match.SendChoices(offer.Id, new List<int> { offer.Entities[pick] });
            AssertNoEmptyResumedBlock(DeckDiscoverWireHistory(capture, cursor), game, name);
            FlushFieldHistory(match);
            if (Environment.GetEnvironmentVariable("HS_DUMP") == "1" && pick == 0 && !secondCard && !discovered)
            {
                Console.WriteLine("DUMP " + name + " offers=" + string.Join(",", offer.Entities));
                foreach (var row in DeckDiscoverWireHistory(capture, 0))
                {
                    int id = SnapshotEntity(row);
                    if (id != 0 && offer.Entities.Contains(id))
                        Console.WriteLine($"  ENTITY {id} {(row.HasFullEntity ? "FULL" : row.HasShowEntity ? "SHOW" : "CHANGE")} premium={SnapshotTag(row, GameTag.PREMIUM)} diamond={SnapshotTag(row, (GameTag)1932)} signature={SnapshotTag(row, (GameTag)2589)} zone={SnapshotTag(row, GameTag.ZONE)}");
                    else if (row.HasTagChange && offer.Entities.Contains(row.TagChange.Entity))
                        Console.WriteLine($"  TAGCHANGE {row.TagChange.Entity} {row.TagChange.Tag}={row.TagChange.Value}");
                }
            }
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

    private static string Row(PowerHistoryData r)
    {
        if (r.HasPowerStart) return "BLOCK_START type=" + r.PowerStart.Type + " src=" + r.PowerStart.Source + " tgt=" + r.PowerStart.Target;
        if (r.HasPowerEnd) return "BLOCK_END";
        if (r.HasFullEntity) return "FULL " + r.FullEntity.Entity + " " + r.FullEntity.Name;
        if (r.HasShowEntity) return "SHOW " + r.ShowEntity.Entity + " " + r.ShowEntity.Name;
        if (r.HasHideEntity) return "HIDE " + r.HideEntity.Entity;
        if (r.HasChangeEntity) return "CHANGE " + r.ChangeEntity.Entity;
        if (r.HasTagChange) return "TAG " + r.TagChange.Entity + " " + r.TagChange.Tag + "=" + r.TagChange.Value;
        if (r.HasMetaData) return "META";
        return "OTHER";
    }
    private static void DumpFlow(string label, string hand, string cardId, int pick)
    {
        var capture = new Capture();
        var scenario = DebugScenario.Parse("humanHand=" + hand + "\nhumanSeat=first");
        var match = new NativeMatch(capture.Send, "mage", "mage", 8427) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer };
        match.Start();
        match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
        var game = match.Game; var player = game.Player1; player.BaseMana = 10; player.UsedMana = 0;
        var source = player.HandZone.First(c => c.Card.Id == cardId);
        FlushFieldHistory(match); Invoke0(match, "SendHumanOptions");
        var before = capture.Last<AllOptions>(14);
        int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(source.Id));
        int c0 = capture.Packets.Count;
        match.SendOption(before.Id, play, 0, -1, 1);
        Console.WriteLine("=== " + label + " PLAY");
        Dump(capture, c0);
        var offer = capture.Last<EntityChoices>(17);
        Console.WriteLine("  CHOICE src=" + offer.Source + " type=" + offer.ChoiceType + " hideChosen=" + offer.HideChosen + " ents=" + string.Join(",", offer.Entities));
        int c1 = capture.Packets.Count;
        match.SendChoices(offer.Id, new List<int> { offer.Entities[pick] });
        Console.WriteLine("=== " + label + " PICK");
        Dump(capture, c1);
    }
    private static void Dump(Capture capture, int from)
    {
        foreach (var (id, msg) in capture.Packets.Skip(from))
        {
            if (id == 19) foreach (var r in ((PowerHistory)msg).List) Console.WriteLine("  [19] " + Row(r));
            else Console.WriteLine("  [" + id + "] " + msg.GetType().Name);
        }
    }

    // #115 (lane 1): a POWER block that resumes a Battlecry after the pick and holds only game/player tag changes never let the
    // client finish the choice. The answer must either change something the client can show, or carry those tags loose.
    private static void AssertNoEmptyResumedBlock(List<PowerHistoryData> rows, EngineGame game, string name)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (!rows[i].HasPowerStart || (int)rows[i].PowerStart.Type != (int)BlockType.POWER) continue;
            bool visible = false;
            for (int j = i + 1; j < rows.Count && !rows[j].HasPowerEnd; j++)
                if (!(rows[j].HasTagChange && (rows[j].TagChange.Entity == game.Id || rows[j].TagChange.Entity == game.Player1.Id || rows[j].TagChange.Entity == game.Player2.Id))) visible = true;
            Check(visible, name + ": the resumed POWER block (source " + rows[i].PowerStart.Source + ") holds only game/player tags and would stall the client");
        }
    }

    // Sweep: every Discover-style Battlecry/spell of the real-Discover list, each offer, resumes without a block the client cannot finish.
    private static void ResumedChoiceSweep()
    {
        int cases = 0;
        foreach (string cardId in new[] { "JAIL_734", "CAP_403", "JAIL_303", "JAIL_851", "TIME_701", "TIME_041", "TIME_034" })
        foreach (int pick in new[] { 0, 1, 2 })
        {
            var capture = new Capture();
            var scenario = DebugScenario.Parse("humanHand=" + cardId + "\nhumanSeat=first");
            var match = new NativeMatch(capture.Send, "mage", "mage", 8427) { DebugScenario = scenario, StartingPlayer = scenario.FirstPlayer };
            match.Start();
            match.SendChoices(capture.Last<EntityChoices>(17).Id, new List<int>());
            var game = match.Game; var player = game.Player1; player.BaseMana = 10; player.UsedMana = 0;
            var source = player.HandZone.First(c => c.Card.Id == cardId);
            FlushFieldHistory(match); Invoke0(match, "SendHumanOptions");
            var before = capture.Last<AllOptions>(14);
            int play = before.Options.FindIndex(o => o.MainOption?.Id == match.WireReturnId(source.Id));
            try { match.SendOption(before.Id, play, 0, -1, 1); } catch (ArgumentException) { match.SendOption(before.Id, play, 0, -1, 0); }
            if (player.Choice == null || player.Choice.ChoiceType != ChoiceType.GENERAL) continue;
            var offer = capture.Last<EntityChoices>(17);
            if (pick >= offer.Entities.Count) continue;
            int cursor = capture.Packets.Count;
            match.SendChoices(offer.Id, new List<int> { offer.Entities[pick] });
            AssertNoEmptyResumedBlock(DeckDiscoverWireHistory(capture, cursor), game, $"{cardId} pick {pick}");
            cases++;
        }
        Check(cases >= 6, "the resumed-choice sweep ran: " + cases);
        Console.WriteLine($"PASS resumed choices: {cases} card/offer cases resume without an empty POWER block (#115)");
    }
}

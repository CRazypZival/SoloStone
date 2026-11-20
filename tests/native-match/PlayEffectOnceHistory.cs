using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Config;
using SabberStoneCore.Kettle;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using EngineZone = SabberStoneCore.Enums.Zone;

internal static partial class Program
{
    // #195: Halazzi, the Lynx played HalazziTheLynx_SpawnToHand once per Lynx. Its Battlecry queues one AddCardTo per free hand
    // slot (EnqueueNumberTask), and each queued repeat ran in a sibling POWER block sourced by Halazzi. The client's
    // PowerProcessor makes a PowerSpellController for every POWER task list and activates the card's play effect in each
    // (PowerSpellController.AddPowerSourceAndTargets / ActivateCardEffects). Retail sends one POWER block per effect index
    // (HearthSim hsreplay-test-data: no PLAY with two same-index POWER blocks of its card outside a doubled Battlecry; Unleash
    // the Hounds, Burgle and Animal Companion create several cards inside one). Every card whose power queues repeats of itself
    // the same way shares the fix (TaskQueue continuation), so the sweep below covers a spell and a summon too. Both seats.
    private static void PlayEffectOnceHistory()
    {
        var failures = new List<string>();
        int cases = 0;
        foreach (bool bot in new[] { false, true })
        foreach (var (id, held, enemies) in new[] {
            ("CORE_TRL_900", 3, 0), ("CORE_TRL_900", 0, 0), ("CORE_TRL_900", 9, 0), ("TRL_900", 3, 0),
            ("EX1_538", 3, 3), ("CORE_GVG_061", 3, 0) })
        {
            string name = $"{id}-held{held}-{(bot ? "opponent" : "owner")}";
            var (match, capture) = AnimationMatch(bot);
            var game = match.Game;
            var owner = game.CurrentPlayer;
            foreach (var old in owner.HandZone.ToArray()) owner.SetasideZone.Add(old.Zone.Remove(old));
            foreach (var minion in owner.Opponent.BoardZone.ToArray()) owner.Opponent.SetasideZone.Add(minion.Zone.Remove(minion));
            for (int i = 0; i < enemies; i++) EngineEntity.FromCard(owner.Opponent, Cards.FromId("CS2_182"), zone: owner.Opponent.BoardZone);
            for (int i = 0; i < held; i++) EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.HandZone);
            var source = EngineEntity.FromCard(owner, Cards.FromId(id), zone: owner.HandZone);
            owner.BaseMana = 10; owner.UsedMana = 0;
            FlushFieldHistory(match);
            int cursor = capture.Packets.Count, hand = owner.HandZone.Count, board = owner.BoardZone.Count;
            InvokeProcess(match, owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD && t.Source?.Id == source.Id));
            FlushFieldHistory(match);
            DumpDrawHistory(name, capture, cursor);
            var rows = History(capture, cursor);
            Check(rows.Count(e => e.HasPowerStart) == rows.Count(e => e.HasPowerEnd), name + ": every block closes");
            // The card's own POWER blocks directly inside its PLAY block, and what each creates.
            var powers = new List<(int Index, int Created)>();
            int depth = 0, play = -1;
            foreach (var row in rows)
            {
                if (row.HasPowerStart)
                {
                    if (depth == 0 && (int)row.PowerStart.Type == (int)BlockType.PLAY && row.PowerStart.Source == source.Id) play = 0;
                    else if (play == 0 && depth == 1 && (int)row.PowerStart.Type == (int)BlockType.POWER && row.PowerStart.Source == source.Id)
                        powers.Add((row.PowerStart.EffectIndex, 0));
                    depth++;
                    continue;
                }
                if (row.HasPowerEnd) { if (--depth == 0) play = -1; continue; }
                if (row.HasFullEntity && play == 0 && depth >= 2 && powers.Count > 0)
                    powers[^1] = (powers[^1].Index, powers[^1].Created + 1);
            }
            if (powers.Count != 1 || powers.Select(p => p.Index).Distinct().Count() != powers.Count)
                failures.Add($"{name}: {powers.Count} POWER blocks of the card in its PLAY ({string.Join(",", powers.Select(p => p.Index + ":" + p.Created))}); "
                    + "the client plays the card's play effect once per block, retail sends one");
            int created = id.EndsWith("TRL_900") ? Math.Min(10 - held, 10) : id == "EX1_538" ? 3 : 0;
            if (id.EndsWith("TRL_900"))
            {
                Check(owner.HandZone.Count(c => c.Card.Id == "TRL_348t") == created && owner.HandZone.Count == hand - 1 + created,
                    name + ": Halazzi fills the hand");
                if (powers.Count == 1 && powers[0].Created != created)
                    failures.Add($"{name}: the POWER block creates {powers[0].Created} Lynxes, expected {created}");
            }
            if (id == "EX1_538") Check(owner.BoardZone.Count == board + 3, name + ": one Hound per enemy minion");
            capture.SerializeAll();
            cases++;
        }
        failures.AddRange(RepeatSweep());
        Check(failures.Count == 0, string.Join("\n", failures));
        Console.WriteLine($"PASS play effect once: {cases} cases plus the {RepeatSweepCards.Length}-card repeat sweep; Halazzi (Core and legacy, hand 0/3/9), Unleash the Hounds, Muster for Battle, Brann + Halazzi; one POWER block per play, both seats");
    }

    // Every card whose power queued its repeats into same-source sibling POWER blocks (a sweep of all 2339 cards playable from
    // hand, 2026-11-19: 62 cards before the summon join, 39 after it). Each play must now carry one POWER block per effect index.
    private static readonly string[] RepeatSweepCards = {
        "BE_183", "BOT_084", "BOT_254", "BOT_536", "BOT_544", "BOT_600", "BOT_601", "BRM_001", "BRM_030", "CFM_621t23", "CFM_621t30", "CFM_621t31",
        "CFM_811", "CORE_GVG_061", "CORE_TRL_900", "DAL_010", "DAL_173", "DAL_177", "DAL_177ts", "EX1_050", "EX1_116", "EX1_161",
        "EX1_248", "EX1_384", "EX1_538", "EX1_562", "EX1_573b", "FP1_019", "GIL_147", "GIL_548", "GIL_553", "GIL_825", "GVG_042", "GVG_045", "GVG_061", "GVG_090",
        "ICC_314t2", "ICC_314t3", "ICC_832a", "ICC_832b", "KAR_026", "LOOT_064t1", "LOOT_064t2", "LOOT_103t1", "LOOT_103t2",
        "LOOT_347", "LOOT_526", "OG_090", "OG_114", "OG_116", "OG_195a", "OG_239", "OG_273", "OG_280", "TIME_027", "TRL_253", "TRL_362", "TRL_900", "UNG_002", "UNG_926" };
    // Not asserted yet: LOOT_054 and UNG_047, whose repeats wait for a player's choice.

    private static List<string> RepeatSweep()
    {
        var failures = new List<string>();
        foreach (string id in RepeatSweepCards)
        {
            var game = new SabberStoneCore.Model.Game(new GameConfig { RandomSeed = 73, StartPlayer = 1, Shuffle = false, SkipMulligan = true,
                History = true, Logging = false, FormatType = FormatType.FT_WILD, Player1HeroClass = CardClass.HUNTER, Player2HeroClass = CardClass.MAGE,
                Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(), Player2Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList() });
            game.StartGame();
            var owner = game.Player1;
            foreach (var old in owner.HandZone.ToArray()) owner.SetasideZone.Add(old.Zone.Remove(old));
            owner.BaseMana = 10; owner.UsedMana = 0;
            EngineEntity.FromCard(owner, Cards.FromId("CS2_172"), zone: owner.BoardZone);
            EngineEntity.FromCard(owner.Opponent, Cards.FromId("CS2_182"), zone: owner.Opponent.BoardZone);
            for (int i = 0; i < 3; i++) EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.HandZone);
            var source = EngineEntity.FromCard(owner, Cards.FromId(id), zone: owner.HandZone);
            var option = owner.Options().FirstOrDefault(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD && t.Source?.Id == source.Id);
            if (option == null) { failures.Add(id + ": sweep fixture cannot play it"); continue; }
            int from = game.PowerHistory.Full.Count;
            game.Process(option);
            int depth = 0; bool inPlay = false; var keys = new List<string>();
            foreach (var e in game.PowerHistory.Full.Skip(from))
            {
                if (e is PowerHistoryBlockStart s)
                {
                    if (depth == 0 && s.BlockType == BlockType.PLAY && s.Source == source.Id) inPlay = true;
                    else if (inPlay && depth == 1 && s.BlockType == BlockType.POWER && s.Source == source.Id) keys.Add(s.Index + "/" + s.EffectIndex);
                    depth++;
                }
                else if (e is PowerHistoryBlockEnd && --depth == 0) inPlay = false;
            }
            if (keys.Count != keys.Distinct().Count())
                failures.Add($"{id}: {keys.Count} POWER blocks of the card in its PLAY share an effect index ({string.Join(",", keys)})");
        }
        failures.AddRange(BrannHalazzi());
        return failures;
    }

    // Brann doubles the Battlecry: two POWER blocks, as retail sends for a doubled Battlecry (two same-index blocks in
    // hslog-tests/39832_blocktype_invalid.power.log:6248). The first holds every Lynx that reaches the hand.
    private static List<string> BrannHalazzi()
    {
        var failures = new List<string>();
        var game = new SabberStoneCore.Model.Game(new GameConfig { RandomSeed = 73, StartPlayer = 1, Shuffle = false, SkipMulligan = true,
            History = true, Logging = false, FormatType = FormatType.FT_WILD, Player1HeroClass = CardClass.HUNTER, Player2HeroClass = CardClass.MAGE,
            Player1Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList(), Player2Deck = Enumerable.Repeat(Cards.FromId("CS2_182"), 30).ToList() });
        game.StartGame();
        var owner = game.Player1;
        foreach (var old in owner.HandZone.ToArray()) owner.SetasideZone.Add(old.Zone.Remove(old));
        owner.BaseMana = 10; owner.UsedMana = 0;
        EngineEntity.FromCard(owner, Cards.FromId("LOE_077"), zone: owner.BoardZone);
        for (int i = 0; i < 3; i++) EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.HandZone);
        var source = EngineEntity.FromCard(owner, Cards.FromId("CORE_TRL_900"), zone: owner.HandZone);
        int from = game.PowerHistory.Full.Count;
        game.Process(owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD && t.Source?.Id == source.Id));
        var powers = new List<int>();
        int depth = 0; bool inPlay = false;
        foreach (var e in game.PowerHistory.Full.Skip(from))
        {
            if (e is PowerHistoryBlockStart s)
            {
                if (depth == 0 && s.BlockType == BlockType.PLAY && s.Source == source.Id) inPlay = true;
                else if (inPlay && depth == 1 && s.BlockType == BlockType.POWER && s.Source == source.Id) powers.Add(0);
                depth++;
            }
            else if (e is PowerHistoryBlockEnd && --depth == 0) inPlay = false;
            else if (inPlay && depth >= 2 && powers.Count > 0 && e is PowerHistoryFullEntity full && full.Entity.Name == "TRL_348t") powers[^1]++;
        }
        // The second Battlecry counts the free slots before the first one's Lynxes arrive, so its Lynxes are made and burnt
        // (engine order, unchanged here); they stay in its own block.
        if (owner.HandZone.Count(c => c.Card.Id == "TRL_348t") != 7 || powers.Count != 2 || powers[0] != 7)
            failures.Add($"Brann + Halazzi: POWER blocks hold {string.Join(",", powers)} Lynxes, expected two blocks, the first with 7");
        return failures;
    }
}

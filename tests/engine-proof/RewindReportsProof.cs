using HsSim;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Decisions;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Tasks.SimpleTasks;
using static Core2026Proof;

internal static partial class RewindProof
{
    private static Game ReportFixture(int seat)
    {
        var game = PinnedPoolFixture.Use(ImbueProof.Make(CardClass.ROGUE, 20));
        if (seat == 2) End(game);
        foreach (var card in game.CurrentPlayer.HandZone.ToArray())
            game.CurrentPlayer.SetasideZone.Add(game.CurrentPlayer.HandZone.Remove(card));
        game.CurrentPlayer.BaseMana = 10;
        return game;
    }

    private static void MorchieHerald()
    {
        foreach (int seat in new[] { 1, 2 })
        foreach (string identity in new[] { "CORE_EDR_004", "CORE_EDR_004_2026" })
        {
            var game = ReportFixture(seat); var player = game.CurrentPlayer;
            Summon(player, "END_036");
            int before = player.HandZone.Count + player.DeckZone.Count;
            var source = Play(game, player, identity);
            while (player.Choice != null && game.Rewind?.Stage != SabberStoneCore.Model.Rewind.RewindStage.Offered)
                PlayerDecisionProof.Answer(game, player.Choice.Choices.First());
            Check(game.Rewind?.KeepBoth == true && player.HandZone.Count + player.DeckZone.Count == before + 1, "#158 first Herald outcome kept");
            Answer(game, true);
            Check(game.Resolution.HasSuspendedEffect && player.Choice != null && player.HandZone.Count + player.DeckZone.Count == before + 1,
                "#158 Morchie repeat must wait for controller's second Discover");
            var futures = new List<string>();
            foreach (var next in new[] { game, game.CloneExact(history: true), PinnedPoolFixture.LoadSnapshot(game.SaveSnapshot()) })
            {
                var owner = next.CurrentPlayer;
                int prompts = 0;
                while (owner.Choice != null)
                {
                    Check(++prompts <= 3, "#158 bounded Dark Gift decisions");
                    var decision = next.GetPendingDecisions().Single();
                    Check(decision.ControllerId == owner.Id && owner.Options().All(t => t is ChooseTask), "#158 NPC sees normal choices");
                    int selected = owner.Choice.Choices.Last();
                    if (seat == 2)
                    {
                        int index = 0;
                        var searched = BotDriver.Decide(next, owner.PlayerId, DeckStyle.Value,
                            state => PilotDecision.From(BotSearch.Choose(state, new BotBudget(1, 3, 32, int.MaxValue))), ref index);
                        Check(searched.Trace.Kind == "search" && searched.Trace.PilotDecision.Nodes > 0, "#158 NPC searches second Discover: " + searched.Trace.FallbackReason);
                        selected = ((ChooseTask)searched.Task).Choices.Single();
                    }
                    PlayerDecisionProof.Answer(next, selected);
                }
                Check(owner.HandZone.Count + owner.DeckZone.Count == before + 2 && owner.NumCardsPlayedThisTurn == 1 && owner.UsedMana == source.Card.Cost,
                    "#158 keep both outcomes, one payment/play");
                Balanced(next);
                futures.Add(Canonical.State(next) + Canonical.Value(next.Random.CaptureState()));
            }
            Check(futures.Distinct().Count() == 1, "#158 suspended repeat load/clone parity");
        }
        Console.WriteLine("PASS #158 Morchie Herald: both seats, player chooses both Discovers/Dark Gifts, load/clone parity");
    }

    private static void MorchieDecisionSweep()
    {
        var inventory = Cards.All.Where(c => c.Implemented && c[GameTag.REWIND] == 1 &&
            c.Power?.PowerTask is DecisionBattlecryTask or DecisionSpellTask or SuspendableTask).Select(c => c.Id).Order().ToArray();
        Check(inventory.SequenceEqual(new[] { "CORE_EDR_004", "CORE_EDR_004_2026", "TIME_EVENT_999" }), "Rewind decision inventory must have sweep coverage");
        foreach (int seat in new[] { 1, 2 })
        foreach (string identity in new[] { "TIME_EVENT_999", "TIME_039", "TIME_770" })
        {
            var game = ReportFixture(seat); var player = game.CurrentPlayer;
            Summon(player, "END_036");
            Add(player.Opponent, "CS2_029"); Add(player.Opponent, "CS2_182");
            var source = Add(player, identity); source[GameTag.REWIND] = 1;
            PlayEntity(game, player, source);
            while (!RewindTestHelpers.IsRewindOffered(game))
            {
                Check(player.Choice != null, identity + " first decision");
                PlayerDecisionProof.Answer(game, player.Choice.Choices.First());
            }
            int plays = player.NumCardsPlayedThisTurn, mana = player.UsedMana, casts = player.NumSpellsPlayedThisGame;
            Answer(game, true);
            Check(game.Resolution.HasSuspendedEffect && player.Choice != null, identity + " repeat awaits decision");
            Check(player.Choice.ChoiceType == (identity == "TIME_770" ? ChoiceType.TARGET : ChoiceType.GENERAL), identity + " decision kind");
            var futures = new List<string>();
            foreach (var next in new[] { game, game.CloneExact(history: true), PinnedPoolFixture.LoadSnapshot(game.SaveSnapshot()) })
            {
                var owner = next.CurrentPlayer;
                int prompts = 0;
                while (owner.Choice != null)
                {
                    Check(++prompts < 5, "bounded repeat decisions");
                    var pending = next.GetPendingDecisions().Single();
                    Reject(next, new DecisionAnswer(pending.Id, owner.Opponent.Id, new[] { pending.Options.First().OptionId }));
                    PlayerDecisionProof.Answer(next, owner.Choice.Choices.Last());
                }
                Check(next.Rewind == null && owner.NumCardsPlayedThisTurn == plays && owner.UsedMana == mana && owner.NumSpellsPlayedThisGame == casts,
                    identity + " effect-only repeat adds no payment/play/cast counters");
                Check(next.PowerHistory.Full.All(e => e is not SabberStoneCore.Kettle.PowerHistoryResetGame), "Morchie emits no reset");
                Balanced(next);
                futures.Add(Canonical.State(next) + Canonical.Value(next.Random.CaptureState()));
            }
            Check(futures.Distinct().Count() == 1, identity + " repeat load/clone parity");
        }
        Console.WriteLine("PASS Morchie decision sweep: CORE_EDR_004, CORE_EDR_004_2026, TIME_EVENT_999; Rewind-tagged TIME_039 Discover and TIME_770 TARGET; both seats, load/clone, invalid owner, no extra play tails");
    }

    private static void BronzeAfterDeaths()
    {
        foreach (int seat in new[] { 1, 2 })
        {
            var game = ReportFixture(seat); var player = game.CurrentPlayer;
            new SabberStoneCore.Tasks.SimpleTasks.ReplaceHeroPower(Cards.FromId("END_000p")).Process(game, player, player.Hero, null);
            var eggs = new[] { Summon(player.Opponent, "DINO_410t3"), Summon(player.Opponent, "DINO_410t3"), Summon(player.Opponent, "DINO_410t4") };
            foreach (var egg in eggs) egg.ToBeDestroyed = true;
            game.DeathProcessingAndAuraUpdate();
            var diagnostics = new List<string>();
            game.RewindBoundaryDiagnostic = diagnostics.Add;
            Console.WriteLine($"INFO #159 after Egg deaths: summoned={game.SummonedMinions.Count}, dead={game.DeadMinions.Count}, queue={game.TaskQueue.IsQuiescent}, resolution={game.Resolution.IsQuiescent}");
            Check(game.Process(HeroPowerTask.Any(player)), "#159 Bronze activation accepted");
            Check(RewindTestHelpers.IsRewindOffered(game), "#159 Bronze must offer Rewind after deathrattle summons");
            Check(diagnostics.Single().Contains("summoned=3") && diagnostics[0].StartsWith("Settling"), "#159 diagnosed exact boundary failure");
            var sink = game.RewindBoundaryDiagnostic;
            Answer(game, true);
            Check(ReferenceEquals(sink, game.RewindBoundaryDiagnostic), "#159 native diagnostic sink survives Rewind restore");
            player = game.CurrentPlayer;
            Check(player.HandZone.Count == 1 && game.Rewind == null, "#159 retry retains one minion and exhausts Rewind");
            Balanced(game);
            End(game); End(game);
            player = game.CurrentPlayer;
            Check(game.Process(HeroPowerTask.Any(player)) && RewindTestHelpers.IsRewindOffered(game), "#159 first use next turn offers again");
            Answer(game, false);
        }
        Console.WriteLine("PASS #159 Bronze after three Egg deathrattles: both seats, first-use offer and retry");
        var busy = ReportFixture(1); var card = Add(busy.CurrentPlayer, "TIME_001");
        var warnings = new List<string>(); busy.RewindBoundaryDiagnostic = message => { warnings.Add(message); throw new Exception("broken sink"); };
        busy.TaskQueue.StartEvent();
        try
        {
            Check(!SabberStoneCore.Model.Rewind.RewindRules.TryBegin(busy, PlayCardTask.Any(busy.CurrentPlayer, card)) &&
                warnings.Single().StartsWith("Skipped") && warnings[0].Contains("queue=False"), "#159 busy skip diagnoses queue; broken observer cannot abort action");
        }
        finally { busy.TaskQueue.EndEvent(); }

        var pending = ReportFixture(1); var owner = pending.CurrentPlayer;
        var tidecaller = Summon(owner, "EX1_509");
        pending.PowerHistory.Last.Clear();
        int attack = tidecaller.AttackDamage;
        Generic.SummonBlock(pending, (Minion)Entity.FromCard(owner, Cards.FromId("EX1_506")), -1, null);
        var spell = Add(owner, "TIME_001");
        Check(pending.Process(PlayCardTask.Any(owner, spell)) && RewindTestHelpers.IsRewindOffered(pending), "#159 settle pending live summon reaction");
        Check(tidecaller.AttackDamage == attack + 1 && pending.PowerHistory.Last.OfType<SabberStoneCore.Kettle.PowerHistoryTagChange>()
            .Any(e => e.EntityId == tidecaller.Id && e.Tag == GameTag.ATK && e.Value == attack + 1), "#159 summon reaction history retained before Rewind action");
        var baseline = PinnedPoolFixture.LoadSnapshot(pending.Rewind.Baseline);
        Check(((Minion)baseline.IdEntityDic[tidecaller.Id]).AttackDamage == attack + 1 && baseline.SummonedMinions.Count == 0,
            "#159 baseline includes settled summon reaction");
        Answer(pending, true);
        Check(((Minion)pending.IdEntityDic[tidecaller.Id]).AttackDamage == attack + 1, "#159 retry does not repeat settled summon reaction");

        var lethal = ReportFixture(1); var victim = lethal.CurrentPlayer;
        Summon(victim, "CS2_237"); // Buzzard draws on the overdue Beast summon.
        foreach (var held in victim.HandZone.ToArray()) victim.SetasideZone.Add(victim.HandZone.Remove(held));
        foreach (var held in victim.DeckZone.ToArray()) victim.SetasideZone.Add(victim.DeckZone.Remove(held));
        var unpaid = Add(victim, "TIME_001"); victim.Hero.Health = 1;
        Generic.SummonBlock(lethal, (Minion)Entity.FromCard(victim, Cards.FromId("CS2_172")), -1, null);
        bool terminalAccepted = lethal.Process(PlayCardTask.Any(victim, unpaid));
        Check(terminalAccepted && lethal.State == State.COMPLETE &&
            victim.UsedMana == 0 && unpaid.Zone == victim.HandZone && lethal.Rewind == null,
            $"#159 lethal pending summon ends match before payment or Rewind action: accepted={terminalAccepted}, state={lethal.State}, mana={victim.UsedMana}, zone={unpaid.Zone.Type}");
        Console.WriteLine("PASS #159 boundary safety: skip diagnostics, observer isolation/restore, pending trigger history, lethal catch-up prevents payment");
    }

    private static void AnnouncerReports()
    {
        foreach (int seat in new[] { 1, 2 })
        foreach (bool morchie in new[] { false, true })
        foreach (bool rewind in new[] { false, true })
        {
            var game = ReportFixture(seat); var player = game.CurrentPlayer;
            if (morchie) Summon(player, "END_036");
            Play(game, player, "TIME_034");
            Answer(game, rewind);
            player = game.CurrentPlayer;
            Check(game.Rewind == null && player.Choice == null && player.Hero.Weapon != null && player.Opponent.Hero.Weapon != null,
                "#80 Announcer settles both weapons");
            Check(player.Options().Any(t => t is EndTurnTask), "#80 End Turn available");
            Balanced(game);
        }
        Console.WriteLine("PASS #80 Stadium Announcer: both seats, Keep/Rewind, with/without Morchie, no freeze");
    }

    internal static void ReportRegressions(string only = null)
    {
        if (only == null || only == "158") MorchieHerald();
        if (only == null || only == "159") BronzeAfterDeaths();
        if (only == null || only == "80") AnnouncerReports();
        if (only == null || only == "sweep") MorchieDecisionSweep();
    }
}

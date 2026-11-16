using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;

internal static partial class Program
{
    private static void MorchieRewindChoices()
    {
        foreach (string card in new[] { "CORE_EDR_004_2026", "TIME_EVENT_999" })
        {
            var (capture, match) = ChoiceRetirementMatch(CardClass.ROGUE);
            var game = match.Game; var player = game.Player1;
            Generic.SummonBlock(game, (Minion)EngineEntity.FromCard(player, Cards.FromId("END_036")), -1, null);
            game.DeathProcessingAndAuraUpdate();
            var source = Generic.DrawCard(player, Cards.FromId(card));
            FlushFieldHistory(match);
            var first = ChoiceRetirementPlay(match, capture, source, card == "TIME_EVENT_999" ? 0 : 1);
            while (player.Choice?.ChoiceAction != ChoiceAction.REWIND)
            {
                var choice = capture.Last<EntityChoices>(17);
                match.SendChoices(choice.Id, new List<int> { choice.Entities[0] });
            }
            Check(game.Rewind?.KeepBoth == true, "Morchie active on wire");
            var offer = capture.Last<EntityChoices>(17);
            match.SendChoices(offer.Id, new List<int> { game.Rewind.RewindEntityId });
            var repeated = capture.Last<EntityChoices>(17);
            Check(player.Choice != null && player.Choice.ChoiceAction != ChoiceAction.REWIND && repeated.Id != first.Id && repeated.Id != offer.Id,
                card + " second effect opens fresh native choice");
            Check(repeated.Entities.Count > 0 && repeated.Entities.All(id => DeckDiscoverWireState(DeckDiscoverWireHistory(capture, 0)).ContainsKey(id)),
                card + " repeated offers exist on wire before click");
            while (player.Choice != null)
            {
                var choice = capture.Last<EntityChoices>(17);
                match.SendChoices(choice.Id, new List<int> { choice.Entities.Last() });
            }
            Check(game.Rewind == null && capture.Last<AllOptions>(14).Options.Any(o => o.Type_ == PegasusGame.Option.Type.END_TURN),
                card + " second choice resumes human options");
            capture.SerializeAll();
            match.StopNpc();
        }
        Console.WriteLine("PASS #158 Morchie native choices: Herald and Sands, second Discover packet, offers before click, resumes End Turn");
        BronzeRewindBoundary();
    }

    private static void BronzeRewindBoundary()
    {
        var (capture, match) = ChoiceRetirementMatch(CardClass.ROGUE);
        var game = match.Game; var player = game.Player1;
        var traces = new List<string>(); match.BotTrace = traces.Add;
        new SabberStoneCore.Tasks.SimpleTasks.ReplaceHeroPower(Cards.FromId("END_000p")).Process(game, player, player.Hero, null);
        var eggs = Enumerable.Range(0, 3).Select(_ => (Minion)EngineEntity.FromCard(player.Opponent, Cards.FromId("DINO_410t3"))).ToArray();
        foreach (var egg in eggs) Generic.SummonBlock(game, egg, -1, null);
        game.DeathProcessingAndAuraUpdate();
        foreach (var egg in eggs) egg.ToBeDestroyed = true;
        game.DeathProcessingAndAuraUpdate();
        InvokeProcess(match, SabberStoneCore.Tasks.PlayerTasks.HeroPowerTask.Any(player));
        FlushFieldHistory(match);
        Check(game.Rewind != null && traces.Any(s => s.StartsWith("REWIND_BOUNDARY Settling") && s.Contains("summoned=3")),
            "#159 native log identifies overdue deathrattle summons and hero power offers Rewind");
        match.StopNpc();
        Console.WriteLine("PASS #159 native boundary diagnostics: three Egg summons, live trace, Bronze Rewind offer");
    }
}

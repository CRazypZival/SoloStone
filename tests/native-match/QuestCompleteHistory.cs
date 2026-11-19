using System;
using System.Collections.Generic;
using System.Linq;
using PegasusGame;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using EngineZone = SabberStoneCore.Enums.Zone;

internal static partial class Program
{
    private static (NativeMatch Match, Capture Capture, int Quest, List<PowerHistoryData> History) QuestCompleteCase(bool bot, string id)
    {
        var (match, capture) = AnimationMatch(bot);
        var game = match.Game;
        var owner = game.CurrentPlayer;
        var quest = (SabberStoneCore.Model.Entities.Spell)EngineEntity.FromCard(owner, Cards.FromId(id), zone: owner.DeckZone);
        FlushFieldHistory(match);
        new SabberStoneCore.Tasks.SimpleTasks.PlayTask(SabberStoneCore.Tasks.SimpleTasks.PlayType.SPELL)
            .Process(game, owner, owner.Hero, null, new TaskStack { Playables = new[] { (IPlayable)quest } });
        FlushFieldHistory(match);
        quest.QuestProgress = quest.QuestTotalProgress - 1;
        FlushFieldHistory(match);
        int cursor = capture.Packets.Count;
        if (id == "TLC_602") InvokeProcess(match, owner.Options().Single(t => t.PlayerTaskType == PlayerTaskType.END_TURN));
        else
        {
            var murloc = EngineEntity.FromCard(owner, Cards.FromId("EX1_506"), zone: owner.HandZone);
            FlushFieldHistory(match);
            InvokeProcess(match, owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD && t.Source?.Id == murloc.Id));
        }
        FlushFieldHistory(match);
        return (match, capture, quest.Id, History(capture, cursor));
    }

    // Playtest 2026-11 (#26): when the opponent completed a quest, the client showed no quest-complete animation and
    // no history tile. Its QuestCompleteSpell needs a TRIGGER block for the quest that holds the reward FULL_ENTITY, and
    // it plays only when that entity's card id is the card the quest names (QUEST_REWARD_DATABASE_ID); the opponent's
    // reward was anonymous. Retail (opposing_maestra and friendly_maestra build 88998, transformed_on_board build 39954)
    // sends progress as a TRIGGER block at effectIndex 1 and completion as a sibling TRIGGER block at effectIndex 0 with
    // triggerKeyword QUEST (462), the reward named inside it. The reward is hidden again at the end of the block.
    private static void QuestCompleteHistory()
    {
        var failures = new List<string>();
        int cases = 0;
        foreach (bool bot in new[] { false, true })
        foreach (var (id, reward) in new[] { ("TLC_602", "TLC_602t"), ("UNG_942", "UNG_942t") })
        {
            string name = $"{id}-{(bot ? "opponent" : "own")}";
            var (match, capture, quest, h) = QuestCompleteCase(bot, id);
            int progress = h.FindIndex(e => e.HasPowerStart && e.PowerStart.Source == quest && (int)e.PowerStart.Type == (int)BlockType.TRIGGER);
            if (progress < 0 || h[progress].PowerStart.EffectIndex != 1 || h[progress].PowerStart.HasTriggerKeyword)
            { failures.Add(name + ": progress must be a TRIGGER block at effectIndex 1 without keyword"); continue; }
            int progressEnd = h.FindIndex(progress + 1, e => e.HasPowerEnd);
            var progressTags = h.Skip(progress + 1).Take(progressEnd - progress - 1).ToList();
            if (progressEnd < 0 || !progressTags.Any(e => e.HasTagChange && e.TagChange.Entity == quest && e.TagChange.Tag == (int)GameTag.QUEST_PROGRESS)
                || progressTags.Any(e => e.HasFullEntity || e.HasShowEntity))
                failures.Add(name + ": progress block holds only the progress tick");
            int complete = progressEnd < 0 ? -1 : h.FindIndex(progressEnd + 1, e => e.HasPowerStart && e.PowerStart.Source == quest);
            if (complete != progressEnd + 1 || h[complete].PowerStart.EffectIndex != 0
                || (int)h[complete].PowerStart.Type != (int)BlockType.TRIGGER || h[complete].PowerStart.TriggerKeyword != (int)GameTag.QUEST)
            { failures.Add(name + ": completion must be the sibling TRIGGER block at effectIndex 0 with keyword 462"); continue; }
            int end = h.FindIndex(complete + 1, e => e.HasPowerEnd);
            var body = h.Skip(complete + 1).Take(end - complete - 1).ToList();
            var full = body.FirstOrDefault(e => e.HasFullEntity);
            if (full == null || full.FullEntity.Name != reward)
            { failures.Add(name + ": the first FULL_ENTITY of the completion block must be the quest's reward " + reward + ", got '" + full?.FullEntity.Name + "'"); continue; }
            var questCard = Cards.FromId(id);
            Check(questCard[GameTag.QUEST_REWARD_DATABASE_ID] == Cards.FromId(reward).AssetId, name + ": quest names this reward card");
            int rewardId = full.FullEntity.Entity;
            bool hidden = body.Any(e => e.HasHideEntity && e.HideEntity.Entity == rewardId);
            bool cleared = body.Any(e => e.HasTagChange && e.TagChange.Entity == rewardId && e.TagChange.Tag == (int)GameTag.REVEALED && e.TagChange.Value == 0);
            if (bot != hidden || bot != cleared)
                failures.Add(name + ": the opponent's reward is hidden again at the end of the block, the owner's stays: hide=" + hidden + " clear=" + cleared);
            if (bot && body.Where(e => e.HasShowEntity && e.ShowEntity.Entity == rewardId).Any(e => e.ShowEntity.Name != reward))
                failures.Add(name + ": the opponent's reward SHOW_ENTITY names the same card");
            if (!body.Any(e => e.HasTagChange && e.TagChange.Entity == quest && e.TagChange.Tag == (int)GameTag.ZONE
                && e.TagChange.Value == (int)EngineZone.GRAVEYARD))
                failures.Add(name + ": the quest leaves for the graveyard inside the completion block");
            // Nothing else of the opponent's hand is named from the completion block on.
            if (bot)
                foreach (var e in h.Skip(complete).Where(e => (e.HasFullEntity || e.HasShowEntity) && SnapshotName(e).Length > 0
                    && SnapshotTag(e, GameTag.ZONE) == (int)EngineZone.HAND && SnapshotTag(e, GameTag.CONTROLLER) == 2
                    && SnapshotEntity(e) != rewardId))
                    failures.Add(name + ": named opponent hand card " + SnapshotEntity(e) + " " + SnapshotName(e));
            failures.AddRange(NativeAdmission.ValidatePackets(capture.HistorySnapshots().Select(p => (IReadOnlyList<PowerHistoryData>)p.List))
                .Select(f => name + ": " + f));
            capture.SerializeAll();
            cases++;
        }
        Check(failures.Count == 0, string.Join("\n", failures));
        Console.WriteLine($"PASS quest complete history: {cases} cases; progress effectIndex 1, completion sibling effectIndex 0 keyword 462, reward named and hidden again for the opponent, both seats, modern and legacy quests");
    }
}

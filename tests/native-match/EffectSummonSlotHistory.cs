using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;

internal static partial class Program
{
    // Retail hsreplay-test-data: 186852_hearthstone_access_eoe.power.log:3333-3338 (Battlecry), :3402-3407
    // (trigger); 34104_subspell.power.log:10709-10711 (Deathrattle), :11268-11359 (spell at row end).
    // Neighbours shift right-most first BEFORE FULL_ENTITY, which already carries the summon's slot; no SHOW_ENTITY.
    private static void EffectSummonSlotHistory()
    {
        var failures = new List<string>();
        int cases = 0;
        foreach (bool bot in new[] { false, true })
        foreach (string id in new[] { "JAIL_877", "JAIL_511", "EX1_556", "CS2_027", "EX1_025", "CATA_725" })
        {
            string name = $"{id}-{(bot ? "opponent" : "owner")}";
            var (match, capture) = AnimationMatch(bot);
            var game = match.Game; var owner = game.CurrentPlayer;
            foreach (var old in owner.HandZone.Concat(owner.BoardZone).Concat(owner.Opponent.BoardZone).ToArray())
                old.Controller.SetasideZone.Add(old.Zone.Remove(old));
            for (int i = 0; i < 3; i++) EngineEntity.FromCard(owner, Cards.FromId("CS2_231"), zone: owner.HandZone);
            var enemy = (Minion)EngineEntity.FromCard(owner.Opponent, Cards.FromId("CS2_182"), zone: owner.Opponent.BoardZone);
            enemy.BaseHealth = 100; enemy.AttackDamage = 0; // Spire's forced attack must leave both minions alive.
            EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.BoardZone); // A
            bool location = id.StartsWith("JAIL_");
            bool playedMinion = id is "EX1_025" or "CATA_725";
            IPlayable source;
            if (location || id == "EX1_556")
                source = EngineEntity.FromCard(owner, Cards.FromId(id), zone: owner.BoardZone);
            else
            {
                if (!playedMinion) EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.BoardZone);
                source = EngineEntity.FromCard(owner, Cards.FromId(id), zone: owner.HandZone);
            }
            EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.BoardZone); // B
            EngineEntity.FromCard(owner, Cards.FromId("CS2_182"), zone: owner.BoardZone); // C
            var existing = owner.BoardZone.Select(m => m.Id).Append(source.Id).ToHashSet();
            FlushFieldHistory(match);
            int cursor = capture.Packets.Count;
            if (location)
                InvokeProcess(match, owner.Options().OfType<ActivateLocationTask>().Single(t => t.Source == source));
            else if (id == "EX1_556")
            {
                ((Minion)source).ToBeDestroyed = true;
                game.DeathProcessingAndAuraUpdate();
            }
            else InvokeProcess(match, PlayCardTask.Any(owner, source, zonePosition: 1));
            FlushFieldHistory(match);
            var rows = History(capture, cursor);
            var summons = owner.BoardZone.Where(m => !existing.Contains(m.Id)).ToArray();
            void Expect(bool ok, string message) { if (!ok) failures.Add(name + ": " + message); }
            Expect(summons.Length == (id == "CS2_027" ? 2 : 1), "expected summon count");
            foreach (var summon in summons)
            {
                int at = rows.FindIndex(e => e.HasFullEntity && e.FullEntity.Entity == summon.Id);
                Expect(at >= 0, summon.Card.Id + " has FULL_ENTITY");
                if (at < 0) continue;
                Expect(SnapshotTag(rows[at], GameTag.ZONE) == (int)SabberStoneCore.Enums.Zone.PLAY
                    && SnapshotTag(rows[at], GameTag.ZONE_POSITION) == summon.ZonePosition + 1,
                    summon.Card.Id + " FULL_ENTITY carries final nonzero slot");
                var open = new Stack<int>();
                for (int i = 0; i < at; i++)
                {
                    if (rows[i].HasPowerStart) open.Push(i);
                    else if (rows[i].HasPowerEnd) open.Pop();
                }
                Expect(open.Count > 0, "summon belongs to an effect block");
                if (open.Count == 0) continue;
                int start = open.Peek(), end = start + 1, depth = 1;
                Expect((int)rows[start].PowerStart.Type == (int)BlockType.POWER
                    || (int)rows[start].PowerStart.Type == (int)BlockType.TRIGGER, "summon stays nested outside predicted PLAY rows");
                for (; end < rows.Count; end++)
                {
                    if (rows[end].HasPowerStart) depth++;
                    if (rows[end].HasPowerEnd && --depth == 0) break;
                }
                var block = rows.Skip(start + 1).Take(end - start - 1).ToArray();
                Expect(!block.Any(e => e.HasTagChange && e.TagChange.Entity == summon.Id
                    && e.TagChange.Tag == (int)GameTag.ZONE_POSITION), "no summon's own position TAG_CHANGE");
                Expect(!rows.Any(e => e.HasShowEntity && e.ShowEntity.Entity == summon.Id), "no summon SHOW_ENTITY");
                var right = Enumerable.Range(summon.ZonePosition + 1, owner.BoardZone.SlotCount - summon.ZonePosition - 1)
                    .Select(i => owner.BoardZone.GetSlot(i)).Where(m => existing.Contains(m.Id)).Reverse().ToArray();
                var shifts = block.Where(e => e.HasTagChange && e.TagChange.Tag == (int)GameTag.ZONE_POSITION
                    && right.Any(m => m.Id == e.TagChange.Entity)).ToArray();
                Expect(shifts.Select(e => e.TagChange.Entity).SequenceEqual(right.Select(m => m.Id)),
                    "every neighbour shift, right-most first");
                Expect(shifts.All(e => rows.IndexOf(e) < at), "neighbours shift before FULL_ENTITY");
                Expect(shifts.All(e => e.TagChange.Value == right.Single(m => m.Id == e.TagChange.Entity).ZonePosition + 1),
                    "neighbours shift to final slots");
            }
            capture.SerializeAll();
            cases++;
        }
        Check(failures.Count == 0, string.Join("\n", failures));
        Console.WriteLine($"PASS effect summon slots: {cases} cases, both seats, location/Deathrattle/spell/Battlecry/Herald");
    }
}

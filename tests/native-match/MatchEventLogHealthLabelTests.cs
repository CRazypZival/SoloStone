using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;

// Owner ruling 2026-11-18: only a real hit is DAMAGE and only a real heal is HEAL; Reborn's 1-Health return and every other
// non-damage Health change (set-Health, halving) is labelled apart (REBORN, HEALTH_SET). The engine's damage / heal meta tells them apart.
internal static partial class Program
{
    private static string Dump(List<string> lines) => "\n" + string.Join("\n", lines);

    private static void HealthLabelsRealDamageAndHeal()
    {
        var (match, _, lines) = LoggedBoard();
        var player = match.Game.Player1;
        var yeti = EngineEntity.FromCard(player, Cards.FromId("CS2_182"), zone: player.BoardZone);
        var archer = EngineEntity.FromCard(player, Cards.FromId("CS2_189"), zone: player.HandZone);
        var touch = EngineEntity.FromCard(player, Cards.FromId("CS2_007"), zone: player.HandZone);
        FlushHistory(match); lines.Clear();
        PlayAndFlush(match, player, archer, yeti);
        PlayAndFlush(match, player, touch, yeti);
        Check(Kind(lines, "DAMAGE").Any(l => l.Contains("target=CS2_182#" + yeti.Id) && l.Contains("amount=1") && l.Contains("hp=5->4")), "real hit stays DAMAGE" + Dump(lines));
        Check(Kind(lines, "HEAL").Any(l => l.Contains("target=CS2_182#" + yeti.Id) && l.Contains("amount=1") && l.Contains("hp=4->5")), "real heal stays HEAL" + Dump(lines));
        Check(!Kind(lines, "HEALTH_SET").Any() && !Kind(lines, "REBORN").Any(), "no health-set label for real hits" + Dump(lines));
    }

    private static void HealthLabelsHuntersMarkIsHealthSet()
    {
        var (match, _, lines) = LoggedBoard();
        var player = match.Game.Player1;
        var yeti = EngineEntity.FromCard(player, Cards.FromId("CS2_182"), zone: player.BoardZone);
        ((ICharacter)yeti).Damage = 2;
        var mark = EngineEntity.FromCard(player, Cards.FromId("CS2_084"), zone: player.HandZone);
        FlushHistory(match); lines.Clear();
        PlayAndFlush(match, player, mark, yeti);
        Check(!Kind(lines, "DAMAGE").Any() && !Kind(lines, "HEAL").Any(), "Hunter's Mark is neither damage nor heal" + Dump(lines));
        Check(Kind(lines, "HEALTH_SET").Any(l => l.Contains("target=CS2_182#" + yeti.Id) && l.Contains("hp=3->1") && l.Contains("by=CS2_084#" + mark.Id)), "Hunter's Mark is HEALTH_SET 3->1" + Dump(lines));
    }

    private static void HealthLabelsHalvingIsHealthSet()
    {
        // Garona Halforcen on main sets the enemy hero's Health to half (no damage event). After PR 203 it adds DAMAGE inside
        // the battlecry block without a damage meta; the same rule (no hit meta, no heal meta) labels it HEALTH_SET.
        var (match, _, lines) = LoggedBoard();
        var player = match.Game.Player1; var enemy = match.Game.Player2;
        var llane = EngineEntity.FromCard(enemy, Cards.FromId("TIME_875t"), zone: enemy.HandZone);
        var garona = EngineEntity.FromCard(player, Cards.FromId("TIME_875"), zone: player.HandZone);
        FlushHistory(match); lines.Clear();
        int before = enemy.Hero.Health;
        PlayAndFlush(match, player, garona);
        Check(enemy.Hero.Health == before / 2, "Garona halved the hero: " + before + " -> " + enemy.Hero.Health);
        Check(!Kind(lines, "DAMAGE").Any() && !Kind(lines, "HEAL").Any(), "halving is neither damage nor heal" + Dump(lines));
        Check(Kind(lines, "STATS").Any(l => l.Contains("target=HERO_08#") && l.Contains("stats=0/" + before + "->0/" + before / 2) && l.Contains("by=TIME_875#" + garona.Id)), "halving is a STATS line " + before + "->" + before / 2 + Dump(lines));
    }

    private static void HealthLabelsDamageWriteWithoutHitIsHealthSet()
    {
        // What Garona Halforcen does after PR 203: it adds DAMAGE to the hero. A DAMAGE write with no damage event in front
        // of it is not a hit, so it reads HEALTH_SET, never DAMAGE.
        var (match, _, lines) = LoggedBoard();
        var enemy = match.Game.Player2;
        enemy.Hero.Damage = 15;
        FlushHistory(match);
        Check(Kind(lines, "HEALTH_SET").Any(l => l.Contains("target=HERO_08#") && l.Contains("hp=30->15")), "bare DAMAGE write is HEALTH_SET" + Dump(lines));
        Check(!Kind(lines, "DAMAGE").Any(), "bare DAMAGE write is not damage" + Dump(lines));
        enemy.Hero.Damage = 5;
        FlushHistory(match);
        Check(Kind(lines, "HEALTH_SET").Any(l => l.Contains("hp=15->25")), "bare DAMAGE drop is HEALTH_SET, not HEAL" + Dump(lines));
        Check(!Kind(lines, "HEAL").Any(), "bare DAMAGE drop is not a heal" + Dump(lines));
    }

    private static void HealthLabelsRebornIsReborn()
    {
        var (match, _, lines) = LoggedBoard();
        var game = match.Game; var player = game.Player1;
        var yeti = EngineEntity.FromCard(player, Cards.FromId("CS2_182"), zone: player.BoardZone);
        yeti[GameTag.REBORN] = 1;
        var fireball = EngineEntity.FromCard(player, Cards.FromId("CS2_029"), zone: player.HandZone);
        FlushHistory(match); lines.Clear();
        PlayAndFlush(match, player, fireball, yeti);
        var returned = player.BoardZone.Single(m => m.Card.Id == "CS2_182");
        Check(((ICharacter)returned).Health == 1 && returned.Id != yeti.Id, "Reborn returned a 1-Health copy");
        Check(Kind(lines, "REBORN").Any(l => l.Contains("target=CS2_182#" + returned.Id) && l.Contains("hp=5->1")), "Reborn return is REBORN" + Dump(lines));
        Check(!Kind(lines, "DAMAGE").Any(l => l.Contains("#" + returned.Id)), "returned copy took no damage" + Dump(lines));
    }
}

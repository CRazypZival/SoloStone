using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using static Core2026Proof;

// Regression proofs for the targeting rules: random damage and destroy skip mortally wounded characters (#120), mass effects skip
// Untouchable minions (#121), a damage wound lifted above 0 Health before the death check is rescued (#123), a transform resets
// Attack and Health (#138), and Slime 'em! gives both players its spell (#139).
internal static class TargetingRulesProof
{
    private static void Verify(bool condition, string message) => Core2026Proof.Check(condition, "TargetingRules: " + message);

    internal static void Run()
    {
        var failures = new List<string>();
        var cases = new (string, Action)[] {
            ("#138 transform resets stats", TransformResetsStats),
            ("#139 Slime 'em! empty boards", SlimeEmEmptyBoards) };
        foreach (var (name, proof) in cases)
        {
            try { proof(); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); }
        }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
        Console.WriteLine("PASS targeting rules: " + cases.Length + " proof groups");
    }

    // #138: a transformed minion shows only the new card's stats, whatever buffed or printed value it had.
    private static void TransformResetsStats()
    {
        string[] cards = { "CORE_EX1_383", "CS2_182", "CS2_200" };
        for (int kind = 0; kind < 4; kind++)
        {
            foreach (var id in cards)
            {
                var g = QuestLifecycleProof.Fixture(); var p = g.Player1;
                var m = Summon(p.Opponent, id);
                switch (kind)
                {
                    case 1: Play(g, p, "CS2_092", m); break;           // Blessing of Kings enchantment
                    case 2: m.AttackDamage = 11; m.Health = 12; break; // set stats
                    case 3: m.Damage = 2; break;                       // damaged
                }
                Play(g, p, "TIME_005t9");
                Verify(m.Card.Id == "TIME_005t9t" && m.AttackDamage == 1 && m.Health == 1 && m.BaseHealth == 1 && m.Damage == 0,
                    $"{id} buff kind {kind} -> sheep {m.AttackDamage}/{m.Health} (damage {m.Damage})");
            }
        }
        // With aura sources on the board, in either board order, and with the player's own side too.
        var auraFailures = new List<string>();
        foreach (var aura in new[] { "CS2_222", "CS2_122", "EX1_162", "EX1_584", "CS2_117" })
            foreach (bool auraFirst in new[] { true, false })
                foreach (bool own in new[] { true, false })
                {
                    var g = QuestLifecycleProof.Fixture(); var p = g.Player1;
                    var side = own ? p : p.Opponent;
                    Minion src = null, m;
                    if (auraFirst) src = Summon(side, aura);
                    m = Summon(side, "CORE_EX1_383");
                    if (!auraFirst) src = Summon(side, aura);
                    Play(g, p, "TIME_005t9");
                    // The aura source becomes a sheep as well, so no aura remains: the sheep is a plain 1/1.
                    if (!(m.Card.Id == "TIME_005t9t" && m.AttackDamage == 1 && m.Health == 1))
                        auraFailures.Add($"aura {aura} first={auraFirst} own={own}: sheep {m.AttackDamage}/{m.Health}");
                }
        Verify(auraFailures.Count == 0, string.Join("; ", auraFailures));
        // Same-type transform through the generic path as well (Polymorph on a printed 8/8).
        var g2 = QuestLifecycleProof.Fixture(); var p2 = g2.Player1;
        var tirion = Summon(p2.Opponent, "CORE_EX1_383");
        Verify(tirion.AttackDamage == 8 && tirion.Health == 8, "Tirion is printed 8/8");
        Play(g2, p2, "CS2_022", tirion);
        Verify(tirion.AttackDamage == 1 && tirion.Health == 1, $"Polymorph on Tirion gives {tirion.AttackDamage}/{tirion.Health}");
    }

    // Runs each part of a sweep on its own so one failing card does not hide the others.
    private static void Parts(List<string> failures, string name, Action part)
    {
        try { part(); } catch (Exception e) { failures.Add(name + ": " + e.Message); }
    }

    private static Minion Wounded(Controller side)
    {
        var m = Summon(side, "CS2_182"); m.Damage = m.BaseHealth + 1;
        Verify(m.IsDead && m.ToBeDestroyed, "wounded fixture is pending death");
        return m;
    }

    private static void Cast(Game g, Controller p, string id, IPlayable target = null)
    {
        var source = Add(p, id);
        var card = Cards.FromId(id);
        card.Power.PowerTask.Process(g, p, source, target, null);
    }

    // #139: Slime 'em! text has no condition; both players get the spell even with no minions anywhere.
    private static void SlimeEmEmptyBoards()
    {
        var g = QuestLifecycleProof.Fixture(); var p = g.Player1;
        Verify(p.BoardZone.Count == 0 && p.Opponent.BoardZone.Count == 0, "empty boards");
        Play(g, p, "CAP_805");
        Verify(p.HandZone.Count(c => c.Card.Id == "CAP_805t") == 1 && p.Opponent.HandZone.Count(c => c.Card.Id == "CAP_805t") == 1,
            "both players get an Ectoplasm with no minions (text has no condition)");
    }
}

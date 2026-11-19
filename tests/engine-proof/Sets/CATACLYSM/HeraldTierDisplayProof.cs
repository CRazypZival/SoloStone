using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.SimpleTasks;
using static Core2026Proof;

// #192. Text variant and definition factor of tiered cards (Soldiers, Herald Colossal appendages), and the resummon tier rule.
internal static class HeraldTierDisplayProof
{
    // Tag 955 after 0..4 Heralds. Lean, derived from the "@" texts ("Herald twice/once to upgrade", final), upgrades at 2 and 4.
    private static readonly int[] Variants = { 0, 1, 0, 1, 2 };
    private static void Check(bool condition, string message) => Core2026Proof.Check(condition, "Herald tier display: " + message);

    [GroupProof("herald-tier-display", ProofKind.Cards)]
    internal static void Run()
    {
        Lean_VariantFollowsCount();
        Lean_AppendageVariantAndFactor();
        Lean_DefinitionFactorLifecycle();
        Unpinned_ResummonTakesCurrentTier();
        Console.WriteLine("PASS Herald tier display: 955 = 0,1,0,1,2 on board/hand/deck Soldiers and appendages; 31010 follows the scaled stats; resummon at current tier");
    }

    private static int Variant(IPlayable entity) => entity[HeraldRules.TextVariant];

    private static void Lean_VariantFollowsCount()
    {
        var g = HeraldProof.New(CardClass.WARRIOR); var p = g.Player1;
        var held = Add(p, "CATA_580t");
        var deck = Entity.FromCard(p, Cards.FromId("CATA_580t"), zone: p.DeckZone);
        var enemy = Add(p.Opponent, "CATA_580t");
        var source = Summon(p, "CS2_182");
        var soldiers = new List<Minion>();
        Check(Variant(held) == 0 && Variant(deck) == 0, "no Herald yet: variant 0");
        for (int count = 1; count <= 4; count++)
        {
            soldiers.Add(HeraldProof.Herald(g, p, source));
            Check(HeraldRules.Count(p) == count, "count " + count);
            foreach (var shown in soldiers.Cast<IPlayable>().Append(held).Append(deck))
                Check(Variant(shown) == Variants[count], $"every live Soldier (board, hand, deck) shows variant {Variants[count]} after {count} Heralds; {shown.Zone?.Type} has {Variant(shown)}");
            Check(Variant(enemy) == 0, "the opponent's Soldier follows the opponent's count");
            var generated = Add(p, "CATA_580t");
            Check(Variant(generated) == Variants[count], "a Soldier created mid-game starts at the current variant");
            p.SetasideZone.Add(p.HandZone.Remove(generated));
        }
        Check(soldiers[0].BaseHealth == soldiers[0].Card.Health && soldiers[3].BaseHealth == 2 * soldiers[3].Card.Health,
            "existing Soldiers keep the tier they were summoned at (only their text changes)");
        p[HeraldRules.Progress] = 5; p.Opponent[HeraldRules.Progress] = 3;
        HeraldProof.Herald(g, p, source);
        Check(soldiers.All(s => Variant(s) == 2), "final text stays after more Heralds");
        var stolen = soldiers[1];
        new ControlTask(EntityType.TARGET).Process(g, p.Opponent, p.Opponent.Hero, stolen, new TaskStack());
        Check(stolen.Controller == p.Opponent && Variant(stolen) == 1, "a Soldier that changes sides shows its new controller's variant");
    }

    private static void Lean_AppendageVariantAndFactor()
    {
        foreach (var (body, arms) in new[] { ("CATA_726", new[] { "CATA_726t", "CATA_726t1" }), ("CATA_150", new[] { "CATA_150t", "CATA_150t1" }),
            ("CATA_151", new[] { "CATA_151t", "CATA_151t1" }), ("CATA_153", new[] { "CATA_153t", "CATA_153t1" }),
            ("CATA_154", new[] { "CATA_154t", "CATA_154t1" }), ("CATA_155", new[] { "CATA_155t", "CATA_155t1" }) })
        {
            Check(arms.All(id => ColossalRules.IsHeraldAppendage(Cards.FromId(id)) && HeraldRules.IsTiered(Cards.FromId(id))),
                body + " appendages are Herald-tiered");
            Check(!ColossalRules.IsHeraldAppendage(Cards.FromId("CATA_432t1")), "other Colossal appendages are not tiered");
            for (int count = 0; count <= 4; count++)
            {
                var g = HeraldProof.New(CardClass.WARRIOR); var p = g.Player1;
                p[HeraldRules.Progress] = count;
                var colossal = (Minion)Entity.FromCard(p, Cards.FromId(body)); Generic.SummonBlock(g, colossal, -1, null);
                g.DeathProcessingAndAuraUpdate();
                var appendages = ColossalRules.AppendagesOf(colossal).ToArray();
                int factor = 1 << Math.Min(2, count / 2);
                Check(appendages.Length == 2 && appendages.All(a => a[ColossalRules.RealizedFactor] == factor && Variant(a) == Variants[count]),
                    $"{body} at {count} Heralds: appendages factor {factor}, variant {Variants[count]}");
                if (count > 3) continue;
                HeraldProof.Herald(g, p);
                Check(appendages.Where(a => a.Zone == p.BoardZone).All(a => Variant(a) == Variants[count + 1] && a[ColossalRules.RealizedFactor] == factor),
                    $"{body}: a Herald updates live appendages' text, not their factor");
            }
        }
    }

    private static void Lean_DefinitionFactorLifecycle()
    {
        var g = HeraldProof.New(CardClass.WARRIOR); var p = g.Player1;
        p[HeraldRules.Progress] = 4;
        var soldier = HeraldProof.Herald(g, p);
        Check(soldier[ColossalRules.RealizedFactor] == 4 && soldier[HeraldRules.RealizedFactor] == 4 && soldier.AttackDamage == 8, "tier 3 Soldier: factor 4 both tags");
        var copy = (Minion)Generic.Copy(p, soldier, soldier, Zone.PLAY); g.DeathProcessingAndAuraUpdate();
        Check(copy[ColossalRules.RealizedFactor] == 4 && copy.BaseHealth == 4, "a board copy keeps the definition factor");
        copy.Silence(); g.DeathProcessingAndAuraUpdate();
        Check(copy[ColossalRules.RealizedFactor] == 0 && copy[HeraldRules.RealizedFactor] == 0
            && copy.AttackDamage == copy.Card.ATK && copy.BaseHealth == copy.Card.Health, "Silence resets the stats to printed and the definition factor with them");
        new ReturnHandTask(EntityType.TARGET).Process(g, p, p.Hero, soldier, new TaskStack());
        Check(soldier.Zone == p.HandZone && soldier[ColossalRules.RealizedFactor] == 0 && soldier.AttackDamage == soldier.Card.ATK,
            "return to hand clears the factor with the stats");
        var other = HeraldProof.Herald(g, p);
        var sheep = (Minion)Generic.ChangeEntityBlock(p, other, Cards.FromId("CS2_182"), true);
        Check(sheep[ColossalRules.RealizedFactor] == 0 && sheep[HeraldRules.RealizedFactor] == 0, "transform clears the definition factor");
    }

    // UNPINNED rule (coordinator, from wiki Herald: upgrades apply only after the Soldier is summoned; Herald cards generated
    // mid-game take the current count): a Soldier entering the board takes its controller's tier at that moment, a resummon
    // (Ectoplasm, Deathrattle) included; a copy keeps its source's realized factor. Lean until a retail trace pins it.
    private static void Unpinned_ResummonTakesCurrentTier()
    {
        var g = HeraldProof.New(CardClass.WARRIOR); var p = g.Player1;
        var source = Summon(p, "CS2_182");
        var soldier = HeraldProof.Herald(g, p, source);
        Check(soldier.BaseHealth == soldier.Card.Health && soldier[HeraldRules.RealizedFactor] == 1, "first Soldier at base tier");
        Play(g, p, "CAP_805");
        var ectoplasm = p.HandZone.Single(c => c.Card.Id == "CAP_805t");
        Check(soldier.Zone == p.GraveyardZone, "Slime 'em! destroys the Soldier");
        HeraldProof.Herald(g, p); HeraldProof.Herald(g, p);
        foreach (var m in p.BoardZone.ToArray()) Kill(g, m);
        Check(HeraldRules.Count(p) == 3 && HeraldRules.Tier(p) == 1, "two more Heralds reach tier 2");
        p.UsedMana = 0;
        PlayEntity(g, p, ectoplasm);
        var back = p.BoardZone.Single(m => m.Card.Id == "CATA_580t");
        Check(back[HeraldRules.RealizedFactor] == 2 && back.AttackDamage == 2 * back.Card.ATK && back.BaseHealth == 2 * back.Card.Health
            && Variant(back) == 1, "Unpinned lean: the resummoned Soldier comes back at the current tier, with the current text");
        p[HeraldRules.Progress] = 4;
        var copy = (Minion)Generic.Copy(p, back, back, Zone.PLAY); g.DeathProcessingAndAuraUpdate();
        Check(copy[HeraldRules.RealizedFactor] == 2 && copy.BaseHealth == back.BaseHealth && copy[ColossalRules.RealizedFactor] == 2,
            "Unpinned lean: a copy keeps its source's factor, not the current tier");
    }
}

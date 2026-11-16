extern alias TransportFixtures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PegasusGame;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Tasks.SimpleTasks;

internal static partial class Program
{
    private static void HeroPowerReplacementContract(bool dump = false)
    {
        var failures = new List<string>();
        void Expect(bool condition, string message) { if (!condition) { failures.Add(message); Console.WriteLine("FAIL " + message); } }
        var heroes = Cards.All.Where(c => c.Type == CardType.HERO && c.Implemented && c[GameTag.HERO_POWER] != 0 && !c.Id.StartsWith("HERO_")
            && OfflinePoolPolicy.Load().Members.Any(m => m.CardId == c.Id && OfflinePoolPolicy.Load().Sets.Contains(m.Set))
            && (c.Collectible || c.Id == "TLC_513t"))
            .Select(c => c.Id).Concat(new[] { "CORE_EX1_323", "TLC_513t", "CATA_190h" }).Distinct().OrderBy(id => id).ToArray();
        Console.WriteLine("SWEEP hero cards: " + string.Join(", ", heroes));
        foreach (string id in heroes)
        foreach (bool bot in new[] { false, true })
        foreach (bool secondary in new[] { false, true })
        {
            int failuresBefore = failures.Count;
            var (capture, match) = ChoiceRetirementMatch(secondary ? CardClass.DEATHKNIGHT : CardClass.WARLOCK);
            if (bot)
            {
                var fixture = AnimationMatch(botTurn: true, deck: secondary ? "player-core-deathknight" : "player-core-warlock");
                match = fixture.Match; capture = fixture.Capture;
                // ActNpc needs a presentation personality; these class fixtures use starter decks without a named NPC.
                typeof(NativeMatch).GetField("npcProfile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(match, NpcCatalog.All[0]);
            }
            var game = match.Game;
            match.StopNpc();
            var owner = game.CurrentPlayer;
            owner.BaseMana = 10; owner.UsedMana = 0;
            void Play(IPlayable card)
            {
                FlushFieldHistory(match);
                if (bot)
                {
                    var task = owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.PLAY_CARD && t.Source?.Id == card.Id);
                    match.ActNpc(new GoldenDecision(BotAction.From(task), 0, 1, 1, "Exhausted", null, null));
                }
                else
                {
                    typeof(NativeMatch).GetMethod("SendHumanOptions", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(match, null);
                    var options = capture.Last<AllOptions>(14);
                    int option = options.Options.FindIndex(o => o.MainOption?.Id == card.Id && o.MainOption.PowerKeyword == 0);
                    Check(option >= 0, id + " contract play option");
                    match.SendOption(options.Id, option, 0, -1, card.Card.Type == CardType.MINION ? 1 : 0);
                    while (owner.Choice != null)
                    {
                        var choice = capture.Last<EntityChoices>(17);
                        match.SendChoices(choice.Id, new List<int> { choice.Entities[0] });
                    }
                }
            }
            if (secondary)
            {
                var thalena = Generic.DrawCard(owner, Cards.FromId("JAIL_446"));
                thalena[GameTag.COST] = 0;
                Play(thalena);
                Check(owner.SecondaryHeroPower?.Card.Id == "JAIL_446hp", "Thal'ena installs actual secondary power");
            }
            int second = owner.SecondaryHeroPower?.Id ?? 0;
            int oldHero = owner.Hero.Id, oldPower = owner.Hero.HeroPower.Id;
            var source = Generic.DrawCard(owner, Cards.FromId(id));
            source[GameTag.COST] = 0; // Leave mana for an immediate power click, without another card/turn refreshing options.
            FlushFieldHistory(match);
            int cursor = capture.Packets.Count;
            Play(source);
            var history = History(capture, cursor);
            string label = id + " " + (bot ? "NPC" : "human") + " secondary=" + secondary;
            if (dump) DumpHeroReplacement(label, source.Id, oldHero, oldPower, history);
            var hero = owner.Hero;
            int power = hero.HeroPower.Id;
            var full = history.Single(e => e.HasFullEntity && e.FullEntity.Entity == power);
            Expect(SnapshotTag(full, GameTag.CREATOR) == hero.Id, label + " power belongs to replacement hero at creation");
            Expect(history.Any(e => e.HasTagChange && e.TagChange.Entity == owner.Id
                && e.TagChange.Tag == (int)GameTag.HERO_ENTITY && e.TagChange.Value == hero.Id), label + " publishes replacement HERO_ENTITY");
            int retirement = history.FindIndex(e => IsZone(e, oldPower, SabberStoneCore.Enums.Zone.SETASIDE));
            Check(retirement >= 0 && retirement < history.IndexOf(full), label + " retires primary before replacement admission");
            Check(SnapshotTag(full, BasicHeroPowerRules.SecondaryIndex) == 0, label + " replacement primary uses slot zero");
            if (id != "CORE_EX1_323")
            {
                var blocks = new Stack<PowerHistoryStart>();
                foreach (var row in history)
                {
                    if (row.HasPowerStart) blocks.Push(row.PowerStart);
                    if (ReferenceEquals(row, full)) Expect(blocks.Peek().Type == HistoryBlock.Type.PLAY
                        && blocks.Peek().Source == source.Id, label + " power admission stays in hero PLAY scope");
                    if (row.HasPowerEnd) blocks.Pop();
                }
            }
            else
            {
                int effect = history.FindIndex(e => e.HasPowerStart && e.PowerStart.Type == HistoryBlock.Type.POWER
                    && e.PowerStart.Source == hero.Id);
                Check(effect >= 0, label + " Jaraxxus opens own POWER");
                CheckJaraxxusTaskList(history, effect, label);
                int end = history.FindIndex(effect + 1, e => e.HasPowerEnd);
                Check(end > effect, label + " Jaraxxus closes own POWER");
                CheckJaraxxusSpawnZones(history.Skip(effect + 1).Take(end - effect - 1).ToList(), owner.PlayerId);
                Check(history.IndexOf(full) > effect && history.IndexOf(full) < end,
                    label + " replacement power FULL stays inside Jaraxxus effect list");
            }
            Check(second == 0 || owner.SecondaryHeroPower?.Id == second, label + " preserves secondary entity");
            var faults = TransportFixtures::TransportInvariants.HeroPowerSlotInvariant.Validate(capture.HistorySnapshots());
            Check(faults.Count == 0, label + " native slots: " + string.Join("; ", faults.Take(3)));
            if (id == "CATA_190h")
            {
                // A packet model must reject the original reported failure, not merely agree with the engine endpoint.
                var missingRetirement = capture.HistorySnapshots().ToList();
                foreach (var packet in missingRetirement) packet.List.RemoveAll(row => IsZone(row, oldPower, SabberStoneCore.Enums.Zone.SETASIDE));
                Check(TransportFixtures::TransportInvariants.HeroPowerSlotInvariant.Validate(missingRetirement).Count > 0,
                    label + " slot invariant rejects a lost old-power retirement");
                if (second != 0)
                {
                    var wrongSlot = capture.HistorySnapshots().ToList();
                    foreach (var row in wrongSlot.SelectMany(p => p.List).Where(row => row.HasFullEntity && row.FullEntity.Entity == second))
                        row.FullEntity.Tags.RemoveAll(tag => tag.Name == (int)BasicHeroPowerRules.SecondaryIndex);
                    Check(TransportFixtures::TransportInvariants.HeroPowerSlotInvariant.Validate(wrongSlot).Count > 0,
                        label + " slot invariant rejects a secondary admitted to slot zero");
                }
            }
            if (!bot && !hero.HeroPower.IsPassiveHeroPower)
            {
                var options = capture.Last<AllOptions>(14);
                int use = options.Options.FindIndex(o => o.MainOption?.Id == power);
                Check(capture.Packets.Skip(cursor).Any(p => p.Id == 14) && use >= 0
                    && options.Options.All(o => o.MainOption?.Id != oldPower), label + " immediate options select new power only");
                var legal = owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER
                    && (t.Source?.Id ?? power) == power);
                match.SendOption(options.Id, use, 0, legal.Target?.Id ?? -1, 0);
                Check(hero.HeroPower.IsExhausted, label + " new power click resolves immediately");
                if (id == "CATA_190h") Check(hero.AttackDamage == 5, label + " Ruthless grants five Attack");
            }
            capture.SerializeAll();
            if (failures.Count == failuresBefore) Console.WriteLine("PASS hero power contract " + label);
        }
        Check(failures.Count == 0, string.Join("; ", failures));
        HeroPowerEffectRoutes();
        HeroPowerMugRoutes();
    }

    private static void HeroPowerEffectRoutes()
    {
        foreach (var heroClass in Cards.HeroClasses.Concat(new[] { CardClass.DEATHKNIGHT, CardClass.DEMONHUNTER }))
        foreach (bool bot in new[] { false, true })
        foreach (bool secondary in new[] { false, true })
        {
            var fixture = AnimationMatch(botTurn: bot, deck: LocalDecks.PlayerCoreStarterDecks.First(d => LocalDecks.HeroClass(d.Hero) == heroClass).Id);
            var match = fixture.Match; var capture = fixture.Capture;
            match.StopNpc();
            var owner = bot ? match.Game.Player2 : match.Game.Player1;
            // Both seats have the selected class: the fixture uses the same hero/deck for each.
            Check(owner.HeroClass == heroClass, "replacement-route seat has expected class");
            int second = secondary ? BasicHeroPowerRules.InstallSecondary(owner, owner.Hero, "JAIL_446hp").Id : 0;
            FlushFieldHistory(match);
            void Inspect(string route)
            {
                FlushFieldHistory(match);
                var faults = TransportFixtures::TransportInvariants.HeroPowerSlotInvariant.Validate(capture.HistorySnapshots());
                Check(faults.Count == 0, heroClass + " " + route + ": " + string.Join("; ", faults.Take(3)));
                Check(second == 0 || owner.SecondaryHeroPower?.Id == second, route + " preserves secondary slot");
                var rows = capture.HistorySnapshots().SelectMany(p => p.List).ToArray();
                foreach (var retired in match.Game.IdEntityDic.Values.OfType<HeroPower>().Where(p => p.Controller == owner
                    && !owner.HeroPowers.Contains(p)))
                {
                    var lastZone = rows.Where(r => SnapshotEntity(r) == retired.Id
                        || r.HasTagChange && r.TagChange.Entity == retired.Id && r.TagChange.Tag == (int)GameTag.ZONE).Last();
                    int zone = lastZone.HasTagChange ? lastZone.TagChange.Value : SnapshotTag(lastZone, GameTag.ZONE);
                    Check(zone != (int)SabberStoneCore.Enums.Zone.PLAY,
                        heroClass + " " + route + " leaves no retired client power in PLAY: " + retired.Id + " " + retired.Card.Id);
                }
            }
            var original = owner.Hero.HeroPower;
            int upgrade = original.Card[(GameTag)1086];
            if (upgrade == 0 && (heroClass == CardClass.DEATHKNIGHT || heroClass == CardClass.DEMONHUNTER))
                upgrade = Cards.FromId(heroClass == CardClass.DEATHKNIGHT ? "HERO_11bp2" : "HERO_10bp2").AssetId;
            Check(upgrade != 0, heroClass + " has a basic upgrade to exercise");
            new ReplaceHeroPower(Cards.FromAssetId(upgrade)).Process(match.Game, owner, owner.Hero, null);
            Inspect("basic upgrade");
            void Play(string id)
            {
                owner.BaseMana = 10; owner.UsedMana = 0;
                var card = Generic.DrawCard(owner, Cards.FromId(id));
                card[GameTag.COST] = 0;
                FlushFieldHistory(match);
                Check(match.Game.Process(PlayCardTask.Any(owner, card)), id + " effect-route play resolves");
            }
            new ReplaceHeroPower(original.Card).Process(match.Game, owner, owner.Hero, null);
            int beforeGenn = owner.Hero.HeroPower.Id;
            Play("CATA_615t");
            Check(owner.Hero.HeroPower.Id != beforeGenn && owner.Hero.HeroPower.Cost == 1,
                "Genn upgrades starting basic power and sets cost");
            Inspect("Genn");
            new ReplaceHeroPower(Cards.FromId("EX1_625t")).Process(match.Game, owner, owner.Hero, null);
            Inspect("ReplaceHeroPower");
            Play("JAIL_EVENT_101");
            int soul = owner.Hero.HeroPower.Id;
            Check(owner.Hero.HeroPower.Card.Id == "JAIL_EVENT_101hp", "Soul Immolation installs power");
            Inspect("Soul Immolation install");
            Play("JAIL_EVENT_101");
            Check(owner.Hero.HeroPower.Id == soul && owner.Hero.HeroPower[(GameTag)2] == 3,
                "Soul Immolation upgrades existing entity");
            Inspect("Soul Immolation upgrade");
            var assembled = SabberStoneCore.Model.Card.CreateAssembledCard(Cards.FromId("CATA_133"), Cards.FromId("CATA_133"), Cards.FromId("CATA_999"));
            var passive = BasicHeroPowerRules.InstallPassive(owner, owner.Hero, assembled);
            Check(passive.IsPassiveHeroPower && owner.Options().All(t => t.PlayerTaskType != PlayerTaskType.HERO_POWER
                || t.Source?.Id == second), "assembled passive has no active primary option");
            Inspect("assembled passive");
            string blessing = ImbueRules.Blessing(heroClass);
            ImbueRules.Imbue(owner, owner.Hero);
            Check(blessing == null ? owner.Hero.HeroPower == passive && ImbueRules.Count(owner) == 0
                : owner.Hero.HeroPower.Card.Id == blessing && owner.Hero.HeroPower != passive && ImbueRules.Count(owner) == 1,
                "Imbue installs class Blessing or leaves unsupported class unchanged");
            Inspect(blessing == null ? "Imbue no-op" : "Imbue install");
            int savedPower = owner.Hero.HeroPower.Id;
            ImbueRules.Imbue(owner, owner.Hero);
            Check(owner.Hero.HeroPower.Id == savedPower && ImbueRules.Count(owner) == (blessing == null ? 0 : 2),
                "subsequent Imbue retains entity and advances only supported classes");
            Inspect(blessing == null ? "Imbue repeated no-op" : "Imbue upgrade");
            Play("TLC_632");
            Inspect("Sulfuras install");
            void UsePrimary()
            {
                owner.UsedMana = 0;
                Check(match.Game.Process(owner.Options().First(t => t.PlayerTaskType == PlayerTaskType.HERO_POWER
                    && (t.Source?.Id ?? owner.Hero.HeroPower.Id) == owner.Hero.HeroPower.Id)), "Sulfuras legal power use resolves");
            }
            int temporary = owner.Hero.HeroPower.Id;
            UsePrimary();
            Check(owner.Hero.HeroPower.Id == temporary && owner.Hero.HeroPower.Card.Id == "TLC_632t2",
                "first Sulfuras use changes identity on same entity");
            Inspect("Sulfuras CHANGE_ENTITY");
            owner.Hero.HeroPower.IsExhausted = false;
            UsePrimary();
            Inspect("Sulfuras restore");
            Check(owner.Hero.HeroPower.Id == savedPower, "temporary power restores original entity");
            capture.SerializeAll();
            Console.WriteLine($"PASS power effect routes {heroClass} {(bot ? "NPC" : "human")} secondary={secondary}");
        }
    }

    private static void HeroPowerMugRoutes()
    {
        foreach (bool bot in new[] { false, true })
        foreach (string filler in new[] { "CS2_106", "CS2_182", "CS2_029" })
        {
            var cards = new[] { "JAIL_800", filler };
            var (match, capture) = SogStart(bot ? Array.Empty<string>() : cards, bot ? cards : Array.Empty<string>());
            SogComplete(match, capture);
            var owner = bot ? match.Game.Player2 : match.Game.Player1;
            var powers = owner.HeroPowers.Select(p => p.Card.Id).ToArray();
            Check(powers.Contains("JAIL_800hp1") == (filler != "CS2_182")
                && powers.Contains("JAIL_800hp2") == (filler != "CS2_029"), "Mug'Zee predicate branches install expected slots");
            var faults = TransportFixtures::TransportInvariants.HeroPowerSlotInvariant.Validate(capture.HistorySnapshots());
            Check(faults.Count == 0, "Mug'Zee slot history: " + string.Join("; ", faults.Take(3)));
            capture.SerializeAll();
            Console.WriteLine($"PASS Mug'Zee {(bot ? "NPC" : "human")} filler={filler} powers={string.Join(",", powers)}");
        }
    }
}

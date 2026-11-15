using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using MiniJSON;
using EngineCard = SabberStoneCore.Model.Card;

// Versioned inputs and stable integer draws keep every offer reproducible across runtimes and save reloads.
public sealed class DraftRules
{
    public static DraftRules Arena(int version = 1) => version == 1 ? arena.Value
        : throw new InvalidOperationException("Unknown Arena rules version.");
    private static readonly Lazy<DraftRules> arena = new Lazy<DraftRules>(() => new DraftRules());
    public int Version { get; }
    public int EntryGold { get; }
    public int DeckSize { get; }
    public int OfferSize { get; }
    public int MaxWins { get; }
    public int MaxLosses { get; }
    private readonly int classWeight;
    private readonly string[] heroes;
    private readonly KeyValuePair<Rarity, int>[] rarities;
    private readonly HashSet<string> denyIds;
    private readonly int[][] rewards;
    // Built on the first offer only: reading the rules (View with no run, validation) never touches the card pool.
    private readonly Lazy<EngineCard[]> pool;

    private DraftRules()
    {
        using (var stream = typeof(DraftRules).Assembly.GetManifestResourceStream("SoloStone.Arena.arena-rules-v1.json"))
        {
            var root = EmbeddedJson.Object(EmbeddedJson.Read(stream));
            Version = Required(root, "version"); EntryGold = Required(root, "entryGold");
            DeckSize = Required(root, "deckSize"); OfferSize = Required(root, "offerSize");
            MaxWins = Required(root, "maxWins"); MaxLosses = Required(root, "maxLosses");
            classWeight = Required(root, "classWeight");
            heroes = EmbeddedJson.Array(root["heroes"]).Cast<string>().ToArray();
            denyIds = new HashSet<string>(EmbeddedJson.Array(root["denyIds"]).Cast<string>(), StringComparer.Ordinal);
            rarities = EmbeddedJson.Array(root["rarities"]).Select(value => {
                var row = EmbeddedJson.Object(value);
                return new KeyValuePair<Rarity, int>((Rarity)Enum.Parse(typeof(Rarity), EmbeddedJson.Text(row, "rarity")), Required(row, "weight"));
            }).ToArray();
            rewards = EmbeddedJson.Array(root["rewards"]).Select((value, index) => {
                var row = EmbeddedJson.Object(value);
                if (Required(row, "wins") != index) throw new InvalidOperationException("Invalid Arena reward order.");
                return new[] { Required(row, "packs"), Required(row, "gold"), Required(row, "dust") };
            }).ToArray();
        }
        if (Version != 1 || EntryGold <= 0 || DeckSize <= 0 || OfferSize < 2 || MaxWins <= 0 || MaxLosses <= 0
            || classWeight <= 0 || heroes.Distinct().Count() != heroes.Length || heroes.Length < OfferSize
            || rarities.Length == 0 || rarities.Any(row => row.Value <= 0 || row.Key == Rarity.LEGENDARY)
            || rewards.Length != MaxWins + 1 || rewards.Any(row => row.Any(value => value < 0)))
            throw new InvalidOperationException("Invalid Arena rules.");
        pool = new Lazy<EngineCard[]>(() => LocalCardCatalog.ActivePoolIds().Select(Cards.FromId).Where(Eligible)
            .OrderBy(card => card.Id, StringComparer.Ordinal).ToArray());
    }

    private static int Required(JsonNode row, string key) =>
        EmbeddedJson.Integer(row, key) ?? throw new InvalidOperationException("Missing Arena rule: " + key);

    public bool Eligible(EngineCard card)
    {
        if (card == null || !card.Collectible || !card.Implemented || denyIds.Contains(card.Id)
            || LocalDecks.FabledBundle(card.Id).Count != 0 || LocalDecks.IsFabledCompanion(card.Id)
            || card[(GameTag)2931] > 0 || card[(GameTag)997] > 0 || card[(GameTag)1048] > 0) return false;
#if ENGINE_SOURCE
        if (SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(card.Id)) return false;
#endif
        string text = card.Text ?? "";
        // Printed deck conditions have no single engine tag (highlander, parity, starting-deck replacements).
        string plain = System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", ""), @"[\s_]+", " ").ToLowerInvariant();
        if (plain.Contains("your deck has") || plain.Contains("your deck contains") || plain.Contains("your deck had")
            || plain.Contains("your starting deck") || plain.Contains("start of game:") || plain.Contains("start of game,")
            || plain.Contains("you started the game with") || plain.Contains("whizbang")) return false;
        return card.Cost <= 10 || !string.IsNullOrWhiteSpace(text);
    }

    public IReadOnlyList<string> Heroes => heroes;
    public IReadOnlyList<string> HeroOffers(long seed) => Draw(heroes.ToList(), seed, 0);

    public IReadOnlyList<string> Offers(long seed, int slot, string hero, IEnumerable<string> deck)
    {
        var ids = deck.ToArray();
        if (!heroes.Contains(hero) || slot < 1 || slot > DeckSize || ids.Length != slot - 1)
            throw new InvalidOperationException("Invalid draft slot or hero.");
        int decision = StableSeed.Derive(seed, Version, slot, Hash(hero), Hash(string.Join("|", ids)));
        var heroClass = LocalDecks.HeroClass(hero);
        var runes = LocalDecks.RuneCounts(ids.Select(Cards.FromId));
        var allowed = pool.Value.Where(card => LocalDecks.IsAllowedInDeck(card, heroClass)
            && (hero != "HERO_11" || Enumerable.Range(0, 3).Sum(i => Math.Max(runes[i], card[(GameTag)(2196 + i)])) <= 3)).ToArray();
        Rarity rarity = Rarity.LEGENDARY;
        if (slot != 1)
        {
            int roll = StableSeed.Index(decision, rarities.Sum(row => row.Value));
            foreach (var row in rarities) { roll -= row.Value; if (roll < 0) { rarity = row.Key; break; } }
        }
        var candidates = allowed.Where(card => card.Rarity == rarity || rarity == Rarity.COMMON && card.Rarity == Rarity.FREE).ToList();
        if (candidates.Count < OfferSize) throw new InvalidOperationException("Draft pool cannot fill this rarity offer.");
        var result = new List<string>();
        for (int i = 0; i < OfferSize; i++)
        {
            int roll = StableSeed.Index(StableSeed.Derive(decision, i), candidates.Sum(card => ClassWeight(card)));
            var chosen = candidates[0];
            foreach (var card in candidates) { roll -= ClassWeight(card); if (roll < 0) { chosen = card; break; } }
            result.Add(chosen.Id); candidates.Remove(chosen);
        }
        return result.AsReadOnly();
    }

    private int ClassWeight(EngineCard card) => card.Class == CardClass.NEUTRAL && card.MultiClassGroup == 0
        && card[(GameTag)476] == 0 ? 1 : classWeight;
    private IReadOnlyList<string> Draw(List<string> choices, long seed, int slot)
    {
        var result = new List<string>();
        for (int i = 0; i < OfferSize; i++) {
            int index = StableSeed.Index(StableSeed.Derive(seed, Version, slot, i), choices.Count);
            result.Add(choices[index]); choices.RemoveAt(index);
        }
        return result.AsReadOnly();
    }
    private static long Hash(string value)
    {
        unchecked { ulong hash = 14695981039346656037UL; foreach (char c in value) { hash ^= c; hash *= 1099511628211UL; } return (long)hash; }
    }

    public ArenaReward Reward(string runId, int wins, string tierId, string packType)
    {
        if (wins < 0 || wins > MaxWins || !FairPackRules.PackTypes.Contains(packType))
            throw new InvalidOperationException("Invalid Arena reward inputs.");
        var row = rewards[wins]; var tier = GrindTiers.Find(tierId);
        return new ArenaReward { RunId = runId, Wins = wins, Gold = tier.Gold(row[1]), Dust = tier.Dust(row[2]),
            PackTypes = Enumerable.Repeat(packType, row[0]).ToList() };
    }
}

public sealed class DraftBotResult
{
    public string Hero;
    public List<string> Deck = new List<string>();
    public double Quality;
}

public static class DraftBot
{
    public static DraftBotResult Draft(long seed, double skill)
    {
        if (double.IsNaN(skill) || skill < 0 || skill > 1) throw new ArgumentOutOfRangeException(nameof(skill));
        var rules = DraftRules.Arena();
        var result = new DraftBotResult { Hero = rules.HeroOffers(seed)[0] };
        for (int slot = 1; slot <= rules.DeckSize; slot++)
        {
            var choices = rules.Offers(seed, slot, result.Hero, result.Deck)
                .Select(id => new { Id = id, Score = Score(Cards.FromId(id), result.Deck) })
                .OrderByDescending(row => row.Score).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
            int index = StableSeed.Index(StableSeed.Derive(seed, slot, 0x424f54), 10000) < (1 - skill) * 8000 ? 1 : 0;
            result.Deck.Add(choices[index].Id); result.Quality += choices[index].Score;
        }
        result.Quality /= rules.DeckSize;
        return result;
    }

    public static double Score(EngineCard card, IEnumerable<string> deck)
    {
        var cards = deck.Select(Cards.FromId).ToArray();
        int bucket = Math.Min(7, card.Cost);
        double[] target = { 1, 3, 6, 6, 5, 4, 2, 3 };
        double value = card.Type == CardType.MINION ? (card.ATK + card.Health) / (double)(card.Cost + 1)
            : card.Type == CardType.WEAPON ? (card.ATK * card[GameTag.DURABILITY] + 1.0) / (card.Cost + 1) : 1.5 + Math.Min(7, card.Cost) * 0.08;
        string text = (card.Text ?? "").ToLowerInvariant();
        foreach (string keyword in new[] { "draw", "discover", "damage", "destroy", "rush", "taunt", "divine shield", "lifesteal", "summon" })
            if (text.Contains(keyword)) value += 0.35;
        return value - 0.4 * (cards.Count(c => Math.Min(7, c.Cost) == bucket) - target[bucket] * cards.Length / 30.0);
    }
}

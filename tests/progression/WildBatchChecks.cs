using System.Text.RegularExpressions;
using SabberStoneCore.CardSets.Client;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

internal static class WildBatchChecks
{
    internal static void Run()
    {
        var directory = "tests/progression/batches/wild";
        var files = Directory.GetFiles(directory, "*.txt").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var ids = ReadManifests(files.Select(File.ReadAllText));
        var cases = ReadCases(Directory.GetFiles("tests/engine-proof", "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        ValidateCases(ids, cases);
        foreach (string id in ids) ValidateCard(id);
        // The card file carries the 37.0 identity of exactly the listed cards (private tools/gen_card_facts.py writes it).
        Check(IdentityIds().OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal)),
            "Wild manifests and the card file's identity rows differ; regenerate the card file");
        int kept = CatalogSets("facts/violet-hold-2026-09.json");
        var identityIds = new HashSet<string>(IdentityIds(), StringComparer.Ordinal);
        foreach (string id in LocalCardCatalog.NewBasicIds.Where(identityIds.Contains))
            Check(Cards.FromId(id).Set == CardSet.CORE && Cards.FromId(id).Rarity == Rarity.FREE, "engine Basic catalog identity: " + id);
        Reject(() => CheckCatalogRows(new[] { ("CS2_025", new Dictionary<int, int> { [183] = 1635, [203] = (int)Rarity.FREE }) }));
        Reject(() => CheckCatalogRows(new[] { ("CS2_025", new Dictionary<int, int> { [183] = (int)CardSet.CORE, [203] = (int)Rarity.COMMON }) }));
        CheckCatalogRows(new[] { ("CS2_025", new Dictionary<int, int> { [183] = (int)CardSet.CORE, [203] = (int)Rarity.FREE }) });
        Console.WriteLine($"PASS Wild identity catalog sets: {kept} NewBasicIds identity rows keep CORE/FREE");
        Reject(() => ValidateCard("CORE_CS2_188"));
        // The shared Standard loader is deliberately nonrecursive: Wild manifests cannot affect its frozen registry.
        Check(StandardBatchData.Load("tests/progression/batches").Values.SelectMany(batch => batch).All(id => !ids.Contains(id)),
            "Wild manifests overlap Standard batches");
        Reject(() => ValidateCases(new[] { "CS2_028" }, Array.Empty<string>()));
        Reject(() => ValidateCases(Array.Empty<string>(), new[] { "CS2_028" }));
        Reject(() => ValidateCases(new[] { "CS2_028" }, new[] { "CS2_028", "CS2_028" }));
        Reject(() => ReadManifests(new[] { "" }));
        Reject(() => ReadManifests(new[] { "CS2_028 CS2_031\n" }));
        Reject(() => ReadManifests(new[] { "# comment\n" }));
        Reject(() => ReadManifests(new[] { "CS2_028\nCS2_028\n" }));
        Reject(() => ReadManifests(new[] { "CS2_028\n", "CS2_028\n" }));
        Reject(() => ReadCases(new[] { "WildSetsProof.Case(id, Scenario);" }));
        Check(ReadCases(new[] { "// WildSetsProof.Case(\"COMMENT\", Scenario);\nWildSetsProof.Case(\"CS2_028\", Scenario);" })
            .SequenceEqual(new[] { "CS2_028" }), "commented cases do not count");
        Console.WriteLine($"PASS Wild batches: groups={files.Length}; cards={ids.Length}; exactly one case per manifest identity; no Standard overlap");
    }

    // LocalCardCatalog's NewBasicIds must stay Set CORE and Rarity FREE: an identity row for one keeps its base CARD_SET
    // (private tools/gen_card_facts.py, SET_KEPT_MANIFESTS). Returns how many identity rows of the file it checked.
    internal static int CatalogSets(string factsPath)
    {
        using var facts = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(factsPath));
        var rows = new List<(string, Dictionary<int, int>)>();
        if (facts.RootElement.TryGetProperty("identity", out var identity))
            foreach (var row in identity.EnumerateArray())
                rows.Add((row.GetProperty("id").GetString(), row.GetProperty("tags").EnumerateArray()
                    .ToDictionary(pair => pair[0].GetInt32(), pair => pair[1].GetInt32())));
        return CheckCatalogRows(rows);
    }

    private static int CheckCatalogRows(IEnumerable<(string Id, Dictionary<int, int> Tags)> rows)
    {
        int count = 0;
        foreach (var (id, tags) in rows.Where(row => LocalCardCatalog.NewBasicIds.Contains(row.Id)))
        {
            Check(tags.GetValueOrDefault(183) == (int)CardSet.CORE && tags.GetValueOrDefault(203) == (int)Rarity.FREE,
                "identity row of a NewBasicIds card must keep Set CORE and Rarity FREE: " + id);
            count++;
        }
        return count;
    }

    private static string[] IdentityIds()
    {
        using var facts = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes("facts/violet-hold-2026-09.json"));
        return facts.RootElement.TryGetProperty("identity", out var rows)
            ? rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray() : Array.Empty<string>();
    }

    private static void ValidateCases(string[] ids, string[] cases)
    {
        Check(cases.Length == cases.Distinct(StringComparer.Ordinal).Count(), "duplicate WildSetsProof case");
        Check(ids.ToHashSet(StringComparer.Ordinal).SetEquals(cases), "Wild manifests and WildSetsProof cases differ: "
            + string.Join(",", ids.Except(cases).Select(id => "missing case " + id).Concat(cases.Except(ids).Select(id => "unlisted " + id))));
    }

    private static void ValidateCard(string id)
    {
        var card = Cards.FromId(id);
        Check(Cards.IsFormatLegal(card, FormatType.FT_WILD), "Wild collectible/scripted identity: " + id);
        Check(!LocalShop.IsStandardCard(card), "active Standard identity in Wild manifest: " + id);
        Check(!EffectModules.ByDbfId.ContainsKey(card.AssetId), "Standard module in legacy Wild manifest: " + id);
    }

    private static string[] ReadManifests(IEnumerable<string> texts)
    {
        var ids = new List<string>();
        foreach (var text in texts)
        {
            var lines = text.TrimEnd('\r', '\n').Split('\n');
            Check(lines.All(line => Regex.IsMatch(line.TrimEnd('\r'), "^[A-Za-z][A-Za-z0-9_]*$")),
                "Wild manifest requires one identity per line, no comments or blanks");
            ids.AddRange(lines.Select(line => line.TrimEnd('\r')));
        }
        Check(ids.Count > 0 && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count, "empty, duplicate or overlapping Wild manifests");
        return ids.ToArray();
    }

    private static string[] ReadCases(IEnumerable<string> sources)
    {
        var ids = new List<string>();
        foreach (string source in sources)
        {
            // Preserve strings while removing comments, so commented-out cases never satisfy membership.
            string code = Regex.Replace(source, "//[^\r\n]*|/\\*[\\s\\S]*?\\*/|\"(?:\\\\.|[^\"\\\\])*\"",
                match => match.Value.StartsWith("//") || match.Value.StartsWith("/*") ? "" : match.Value);
            var calls = Regex.Matches(code, @"\bWildSetsProof\.Case\s*\(");
            var literalCalls = Regex.Matches(code, "\\bWildSetsProof\\.Case\\s*\\(\\s*\"([A-Za-z][A-Za-z0-9_]*)\"");
            Check(calls.Count == literalCalls.Count, "WildSetsProof cases require literal identities for manifest checks");
            ids.AddRange(literalCalls.Select(match => match.Groups[1].Value));
        }
        return ids.ToArray();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception) { return; }
        throw new Exception("Wild manifest/case negative control was accepted");
    }
}

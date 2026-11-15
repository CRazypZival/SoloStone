using System.Text.Json;
using System.Text.RegularExpressions;
using HsSim;
using SabberStoneCore.Enums;

// Renewal of a pinned transport replay (tests/transport-invariants/replays) after an intended rules change.
//   EngineProof.dll --renew-replay PATH [--dump DIR] [--baseline DIR [--accept "LABEL;LABEL" [--accept-behaviour "SHA: reason"] --write]]
// It replays the recorded actions on the current engine and reports where the legal-move hash chain and the final state/RNG
// fingerprint drift. --dump writes each step's canonical state, chosen move and RNG state (run it on the last passing commit);
// --baseline compares this engine with such a dump and prints what drifted, by label. --write rewrites only `legalShas` and
// `engineFingerprint`; it needs a baseline and --accept naming exactly the printed labels. It refuses when a recorded action is no
// longer legal, when the chosen move at a step changed, when any RNG state differs from the baseline, and, without
// --accept-behaviour "SHA: reason", when the turn count or the winner changes. Exit 1 reports drift without writing.
internal static partial class PinnedReplayEngineProof
{
    private static string Opt(string[] args, string name) { int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }

    internal static int Renew(string[] args)
    {
        string path = Path.GetFullPath(args[Array.IndexOf(args, "--renew-replay") + 1]);
        string dump = Opt(args, "--dump"), baseline = Opt(args, "--baseline"), accept = Opt(args, "--accept"), behaviour = Opt(args, "--accept-behaviour");
        bool write = args.Contains("--write");
        if (write && baseline == null) { Console.Error.WriteLine("--write needs --baseline DIR: see what drifted first"); return 1; }
        if (dump != null) Directory.CreateDirectory(dump);
        string text = File.ReadAllText(path);
        var replay = JsonSerializer.Deserialize<PinnedReplay>(text, Json);
        var record = replay.Game;
        var game = Simulation.Create(record, replay: true, playerDeck: replay.PlayerDeck, opponentDeck: replay.OpponentDeck);
        var legalNow = new List<string>();
        var drift = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var problems = new List<string>();
        int step = 0, firstDrift = -1;
        while (game.State != State.COMPLETE)
        {
            if (game.Turn > 200 || step >= 20000) { problems.Add("game exceeded termination bounds at step " + step); break; }
            var legal = Simulation.Legal(game);
            string hash = Canonical.Sha(Canonical.Value(legal.Select(Simulation.TaskKey).ToArray()));
            if (step >= record.Actions.Count) { problems.Add($"step {step}: the game is not over but the recording has no action left"); break; }
            if (record.Actions[step] < 0 || record.Actions[step] >= legal.Count) { problems.Add($"step {step}: recorded action {record.Actions[step]} is no longer legal ({legal.Count} legal moves)"); break; }
            legalNow.Add(hash);
            string chosen = Simulation.TaskKey(legal[record.Actions[step]]);
            string rng = Canonical.Value(game.Random.CaptureState());
            string state = Canonical.State(game);
            string snapshot = "action:" + chosen + "\nrng:" + rng + "\n" + state;
            if (dump != null) File.WriteAllText(Path.Combine(dump, step.ToString("000") + ".txt"), snapshot);
            bool moved = step >= record.LegalShas.Count || hash != record.LegalShas[step];
            if (moved && firstDrift < 0) firstDrift = step;
            if (baseline != null)
            {
                string old = File.ReadAllText(Path.Combine(baseline, step.ToString("000") + ".txt"));
                string[] oldHead = old.Split('\n', 3), nowHead = snapshot.Split('\n', 3);
                if (oldHead[0] != nowHead[0]) problems.Add($"step {step}: the chosen move changed ({oldHead[0]} -> {nowHead[0]})");
                if (oldHead[1] != nowHead[1]) problems.Add($"step {step}: the RNG state differs");
                if (oldHead[2] != nowHead[2])
                    foreach (var change in StateDiff(oldHead[2], nowHead[2]))
                    {
                        if (!drift.TryGetValue(change.Key, out var seen)) drift[change.Key] = seen = new List<string>();
                        seen.Add("#" + step + " " + change.Value);
                    }
            }
            try { Simulation.Process(game, legal[record.Actions[step]]); }
            catch (Exception error) { problems.Add($"step {step}: action failed: {error.Message}"); break; }
            step++;
        }
        var current = new GameRecord(); Simulation.Finish(current, game);
        string fingerprint = Fingerprint(game);
        var behaviourChanges = new List<string>();
        if (problems.Count == 0 && step != record.Actions.Count) problems.Add($"the game ended after {step} of {record.Actions.Count} recorded actions");
        if (current.Turns != record.Turns) behaviourChanges.Add($"turns {record.Turns} -> {current.Turns}");
        if (current.Winner != record.Winner) behaviourChanges.Add($"winner {record.Winner} -> {current.Winner}");
        int changedHashes = 0;
        for (int i = 0; i < legalNow.Count; i++) if (i >= record.LegalShas.Count || legalNow[i] != record.LegalShas[i]) changedHashes++;
        Console.WriteLine($"{Path.GetFileName(path)}: {changedHashes} of {legalNow.Count} legal-move hashes differ" + (firstDrift < 0 ? "" : $", first at step {firstDrift}")
            + $"; fingerprint {(fingerprint == replay.EngineFingerprint ? "unchanged" : "changed")}");
        foreach (string problem in problems) Console.WriteLine("  PROBLEM " + problem);
        foreach (string change in behaviourChanges) Console.WriteLine("  BEHAVIOUR " + change);
        foreach (var change in drift)
            Console.WriteLine("  " + change.Key + ": " + change.Value.Select(v => v.Split(' ')[0]).Distinct().Count() + " steps, " + change.Value.Count + " changes, first " + change.Value[0]);
        if (changedHashes == 0 && fingerprint == replay.EngineFingerprint && problems.Count == 0 && behaviourChanges.Count == 0) return 0;
        if (problems.Count != 0) { Console.Error.WriteLine("refusing to write: the recording no longer replays as recorded"); return 1; }
        if (baseline == null) Console.WriteLine("  no --baseline: run --dump on the last passing commit to see what drifted");
        if (!write) return 1;
        var labels = drift.Keys.ToArray();
        if (!new HashSet<string>((accept ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)).SetEquals(labels))
        { Console.Error.WriteLine("refusing to write: --accept must name exactly: " + string.Join(";", labels)); return 1; }
        if (behaviourChanges.Count != 0 && !Regex.IsMatch(behaviour ?? "", "^[0-9a-f]{7,40}: \\S.{9,}$"))
        { Console.Error.WriteLine("refusing to write: " + string.Join("; ", behaviourChanges) + " change the game; a deliberate change needs --accept-behaviour \"SHA: reason\""); return 1; }
        var legalPattern = new Regex("\"legalShas\":\\[[^\\]]*\\]");
        if (!legalPattern.IsMatch(text) || !text.Contains($"\"engineFingerprint\":\"{replay.EngineFingerprint}\"", StringComparison.Ordinal))
        { Console.Error.WriteLine("refusing to write: the replay file is not in the expected compact form"); return 1; }
        string renewed = legalPattern.Replace(text, "\"legalShas\":[" + string.Join(",", legalNow.Select(h => "\"" + h + "\"")) + "]", 1);
        renewed = renewed.Replace($"\"engineFingerprint\":\"{replay.EngineFingerprint}\"", $"\"engineFingerprint\":\"{fingerprint}\"", StringComparison.Ordinal);
        if (behaviourChanges.Count != 0) // accepted with a reason: the stored outcome follows
            renewed = Regex.Replace(Regex.Replace(renewed, "\"turns\":\\d+", "\"turns\":" + current.Turns, RegexOptions.None), "\"winner\":\\d+", "\"winner\":" + current.Winner);
        File.WriteAllText(path, renewed);
        Console.WriteLine($"rewrote {changedHashes} legal-move hashes and the fingerprint ({replay.EngineFingerprint} -> {fingerprint}); actions and decks untouched" + (behaviourChanges.Count != 0 ? "; " + string.Join(", ", behaviourChanges) + " accepted" : "; turns and winner untouched"));
        return 0;
    }

    // Proof of the tool itself, on temporary copies of a pinned replay that stores a fingerprint and still matches: an illegal recorded action and a
    // changed winner are refused with the file left alone; tampered hashes and fingerprint are rewritten back, and nothing else in the file changes.
    internal static void RenewSelfTest()
    {
        string source = Directory.GetFiles("tests/transport-invariants/replays", "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .First(f => !f.Contains("transport-debt") && File.ReadAllText(f).Contains("\"engineFingerprint\":\"") && !File.ReadAllText(f).Contains("\"engineFingerprint\":null")
                && Renew(new[] { "--renew-replay", f }) == 0);
        string original = File.ReadAllText(source);
        string dir = Path.Combine(Path.GetTempPath(), "renew-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string baseline = Path.Combine(dir, "baseline");
            Check(Renew(new[] { "--renew-replay", source, "--dump", baseline }) == 0, "renew: a matching replay dumps and reports no drift");
            string temp = Path.Combine(dir, "copy.json");
            var actions = new Regex("\"actions\":\\[(\\d+),").Match(original);
            Check(actions.Success, "renew: replay has an action list");
            File.WriteAllText(temp, original.Replace(actions.Value, "\"actions\":[9999,"));
            string illegal = File.ReadAllText(temp);
            Check(Renew(new[] { "--renew-replay", temp, "--baseline", baseline, "--accept", "", "--write" }) == 1 && File.ReadAllText(temp) == illegal, "renew: an illegal recorded action is refused and nothing is written");
            string fingerprint = new Regex("\"engineFingerprint\":\"([0-9a-f]+)\"").Match(original).Groups[1].Value;
            string firstHash = new Regex("\"legalShas\":\\[\"([0-9a-f]+)\"").Match(original).Groups[1].Value;
            string tampered = original.Replace(fingerprint, new string('0', fingerprint.Length)).Replace("\"legalShas\":[\"" + firstHash, "\"legalShas\":[\"" + new string('1', firstHash.Length));
            File.WriteAllText(temp, tampered);
            Check(Renew(new[] { "--renew-replay", temp }) == 1, "renew: tampered hashes are reported as drift without --write");
            Check(Renew(new[] { "--renew-replay", temp, "--baseline", baseline, "--accept", "", "--write" }) == 0, "renew: tampered hashes are rewritten");
            Check(File.ReadAllText(temp) == original, "renew: only the hashes and the fingerprint were rewritten (the file is byte-identical to the original again)");
            var winner = new Regex("\"winner\":(\\d+)").Match(original);
            Check(winner.Success, "renew: replay stores a winner");
            File.WriteAllText(temp, original.Replace(winner.Value, "\"winner\":" + (winner.Groups[1].Value == "1" ? 2 : 1)));
            string changedWinner = File.ReadAllText(temp);
            Check(Renew(new[] { "--renew-replay", temp, "--baseline", baseline, "--accept", "", "--write" }) == 1 && File.ReadAllText(temp) == changedWinner, "renew: a changed winner is refused without --accept-behaviour");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS pinned replay renewal tool: refuses an illegal action and a changed winner, rewrites only hashes and the fingerprint");
    }

    // Labels: "CARDID tag N" per differing tag, "entity added/removed", "entity card". Example text names the entity id and the values.
    internal static IEnumerable<KeyValuePair<string, string>> StateDiff(string before, string after)
    {
        static Dictionary<int, (string Card, JsonElement Tags)> Entities(string text)
        {
            var map = new Dictionary<int, (string, JsonElement)>();
            foreach (string line in text.Split('\n'))
            {
                if (!line.StartsWith('{')) continue;
                string json = line; int bar = json.IndexOf('|'); if (bar > 0) json = json[..bar];
                try
                {
                    var root = JsonDocument.Parse(json).RootElement;
                    map[root.GetProperty("Id").GetInt32()] = (root.TryGetProperty("CardId", out var card) ? card.GetString() : null, root.GetProperty("Tags"));
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            }
            return map;
        }
        var a = Entities(before); var b = Entities(after);
        var result = new List<KeyValuePair<string, string>>();
        foreach (int id in a.Keys.Union(b.Keys).OrderBy(i => i))
        {
            bool inA = a.TryGetValue(id, out var x), inB = b.TryGetValue(id, out var y);
            if (!inA || !inB) { result.Add(new("entity " + (inA ? "removed" : "added"), $"entity {id} {(inA ? x.Card : y.Card)}")); continue; }
            if (x.Card != y.Card) result.Add(new("entity card", $"entity {id} {x.Card} -> {y.Card}"));
            var ta = x.Tags.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
            var tb = y.Tags.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
            foreach (string tag in ta.Keys.Union(tb.Keys).OrderBy(t => t, StringComparer.Ordinal))
            {
                ta.TryGetValue(tag, out string va); tb.TryGetValue(tag, out string vb);
                if (va != vb) result.Add(new($"{y.Card} tag {tag}", $"entity {id} {va ?? "absent"} -> {vb ?? "absent"}"));
            }
        }
        return result;
    }
}

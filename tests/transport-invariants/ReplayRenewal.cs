using System.Text.Json;

namespace TransportInvariants;

// Tail renewal of a pinned transport replay (replays/) after an intended rules change, when the recorded game legitimately no
// longer plays the same. It never runs in a normal test run: only
//   dotnet TransportInvariants.dll --renew-replay PATH [--from-step N] [--seed N] [--write | --out FILE]
// The recorded actions are replayed on the current engine up to the first one that is no longer legal (or up to step N, which
// must come no later than that). From there a seeded random policy plays the game to its end; the seed is stored in the file
// ("renewal": {"fromStep", "seed"}) so the renewal can be redone. The legal-move hash of every step, the engine fingerprint (when the
// file has one), the findings hash, the turns and the winner are re-recorded; the decks, the pool pin and the seed of the game are not.
// Without --write or --out nothing is written: the tool prints what would change. A replay that still plays as recorded is written back
// byte for byte (hashes that only drifted are re-recorded). The tool re-records; it does not decide. A renewal needs a reviewed
// pinned retail rule, as a golden does (docs/TESTING.md, "Goldens").
internal static class ReplayRenewal
{
    internal sealed record Outcome(string Text, string Before, string After, int Divergence, int Kept, int FirstHashDrift, TransportReplay Replay);

    internal static int Run(string[] args)
    {
        string path = args.Length > 1 && !args[1].StartsWith("--") ? Path.GetFullPath(args[1]) : throw new ArgumentException(Usage);
        int? fromStep = null; long? seed = null; string output = null; bool write = false;
        for (int i = 2; i < args.Length; i++)
            switch (args[i])
            {
                case "--from-step" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n >= 0: fromStep = n; i++; break;
                case "--seed" when i + 1 < args.Length && long.TryParse(args[i + 1], out long s): seed = s; i++; break;
                case "--out" when i + 1 < args.Length: output = Path.GetFullPath(args[++i]); break;
                case "--write": write = true; break;
                default: throw new ArgumentException(Usage);
            }
        if (write && output != null) throw new ArgumentException("--write and --out are exclusive");
        var outcome = Renew(File.ReadAllText(path), fromStep, seed);
        Console.WriteLine(Path.GetFileName(path) + ": " + (outcome.Divergence < 0 ? "the recording still plays; no tail was re-recorded" : $"tail re-recorded from step {outcome.Divergence}"));
        Console.WriteLine("  before: " + outcome.Before);
        Console.WriteLine("  after:  " + outcome.After);
        Console.WriteLine("  first legal-move hash drift: " + (outcome.FirstHashDrift < 0 ? "none" : "step " + outcome.FirstHashDrift)
            + "; unchanged steps: " + outcome.Kept + "; file " + (outcome.Text == File.ReadAllText(path) ? "unchanged" : "changes"));
        string target = write ? path : output;
        if (target == null) { Console.WriteLine("  dry run: nothing written (--write rewrites the file, --out FILE writes a copy)"); return 0; }
        File.WriteAllText(target, outcome.Text);
        Console.WriteLine("  wrote " + target);
        return 0;
    }

    internal const string Usage = "usage: dotnet TransportInvariants.dll --renew-replay PATH [--from-step N] [--seed N] [--write | --out FILE]";

    // The renewed file text and the before/after summary; writes nothing.
    internal static Outcome Renew(string text, int? fromStep, long? seed)
    {
        var replay = JsonSerializer.Deserialize<TransportReplay>(text, Program.Json);
        if (replay?.Schema != 1 || replay.Game?.Actions == null || replay.Game.Actions.Count == 0 || replay.Game.LegalShas == null)
            throw new ArgumentException("not a step-by-step transport replay (transport-debt replays store no actions)");
        var record = replay.Game;
        if (fromStep > record.Actions.Count) throw new ArgumentException($"--from-step {fromStep} is past the {record.Actions.Count} recorded actions");
        if ((replay.Focused || record.Player.StartsWith("standard-coverage-", StringComparison.Ordinal)) && fromStep < 3)
            throw new ArgumentException("a forced-card replay is arranged at step 2; renew from step 3 or later");
        string Summary() => $"turns {record.Turns}, winner {record.Winner}, {record.Actions.Count} actions, fingerprint {replay.EngineFingerprint ?? "none"}";
        string before = Summary();
        long tailSeed = seed ?? unchecked(record.Seed ^ 0x52454E4557L);
        var plan = new RenewPlan { FromStep = fromStep, TailSeed = unchecked((ulong)tailSeed) };
        new Runner().Run(replay, renew: plan);
        replay.Renewal = plan.Divergence >= 0 ? new TailRenewal { FromStep = plan.Divergence, Seed = tailSeed } : null;
        int kept = plan.Divergence >= 0 ? plan.Divergence : record.Actions.Count;
        string renewed = JsonSerializer.Serialize(replay, Program.Json);
        // Pinned files predate the golden-only decisionsSha field and do not carry it; do not add a null one.
        if (replay.Game.DecisionsSha == null && !text.Contains("\"decisionsSha\"", StringComparison.Ordinal))
            renewed = renewed.Replace(",\"decisionsSha\":null", "", StringComparison.Ordinal);
        return new(renewed + "\n", before, Summary(), plan.Divergence, kept, plan.FirstHashDrift, replay);
    }
}

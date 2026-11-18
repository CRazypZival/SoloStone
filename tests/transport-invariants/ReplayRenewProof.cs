using System.Text.Json;

namespace TransportInvariants;

// Headless checks of the tail renewal tool (ReplayRenewal.cs) on the pinned Felhunter replay and temporary copies of it.
internal static class ReplayRenewProof
{
    internal static void Run()
    {
        const string source = "tests/transport-invariants/replays/deathrattle/standard-batch-1-standard-batch-1-21-vs-standard-batch-1-25-s4-random.json";
        string text = File.ReadAllText(source);
        var original = JsonSerializer.Deserialize<TransportReplay>(text, Program.Json);
        int total = original.Game.Actions.Count;

        var same = ReplayRenewal.Renew(text, null, null);
        SelfChecks.Check(same.Text == text && same.Divergence < 0 && same.Kept == total && same.FirstHashDrift < 0,
            "renewing an unchanged replay reproduces the file byte for byte and re-records no tail");

        const int cut = 40;
        var forced = ReplayRenewal.Renew(text, cut, null);
        SelfChecks.Check(forced.Divergence == cut && forced.Kept == cut && forced.Replay.Renewal is { FromStep: cut }
            && forced.Replay.Game.Actions.Take(cut).SequenceEqual(original.Game.Actions.Take(cut))
            && forced.Replay.Game.Actions.Count == forced.Replay.Game.LegalShas.Count
            && forced.Replay.Game.Seed == original.Game.Seed && forced.Replay.PlayerDeck != null,
            "--from-step keeps the recorded steps before it and records the seed of the tail");

        // A step that is no longer legal: the tail starts exactly there.
        var broken = JsonSerializer.Deserialize<TransportReplay>(text, Program.Json);
        broken.Game.Actions[cut] = 100000;
        string brokenText = JsonSerializer.Serialize(broken, Program.Json) + "\n";
        var renewed = ReplayRenewal.Renew(brokenText, null, null);
        var again = ReplayRenewal.Renew(brokenText, null, null);
        SelfChecks.Check(renewed.Divergence == cut && renewed.Kept == cut && renewed.Replay.Renewal is { FromStep: cut, Seed: not 0 }
            && renewed.Replay.Game.Actions.Take(cut).SequenceEqual(original.Game.Actions.Take(cut))
            && renewed.Replay.Game.Actions[cut] != 100000 && renewed.Replay.Game.Turns > 0,
            "a replay whose step is no longer legal renews from that step");
        SelfChecks.Check(again.Text == renewed.Text && ReplayRenewal.Renew(brokenText, null, 7).Text != renewed.Text,
            "a renewal is deterministic for its seed and follows a different seed");

        // The renewed file replays and verifies as an ordinary pinned replay.
        var check = JsonSerializer.Deserialize<TransportReplay>(renewed.Text, Program.Json);
        new Runner().Run(check, verify: true);
        SelfChecks.Check(true, "a renewed replay passes the verifying replay");

        // The switch: no write without --write or --out, and the file is replaced only with --write.
        string dir = Path.Combine(Path.GetTempPath(), "renew-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string temp = Path.Combine(dir, "replay.json"); File.WriteAllText(temp, brokenText);
            ReplayRenewal.Run(new[] { "--renew-replay", temp });
            SelfChecks.Check(File.ReadAllText(temp) == brokenText, "without --write nothing is written");
            string copy = Path.Combine(dir, "copy.json");
            ReplayRenewal.Run(new[] { "--renew-replay", temp, "--out", copy });
            SelfChecks.Check(File.ReadAllText(temp) == brokenText && File.ReadAllText(copy) == renewed.Text, "--out writes a copy and leaves the source alone");
            ReplayRenewal.Run(new[] { "--renew-replay", temp, "--write" });
            SelfChecks.Check(File.ReadAllText(temp) == renewed.Text, "--write replaces the file with the renewal");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS replay tail renewal");
    }
}

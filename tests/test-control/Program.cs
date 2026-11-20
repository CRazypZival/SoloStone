using MiniJSON;
using UnityEngine;

NativeTestControl.CheckBoardTrace();

internal sealed partial class NativeTestControl
{
    internal static void CheckBoardTrace()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        string Request(string command, string fields = "") => "{\"v\":1,\"id\":\"trace\",\"cmd\":\"" + command + "\"" + fields + "}";
        foreach (int frames in new[] { 1, 1200 })
        {
            var request = TestControlProtocol.Parse(Request("board-trace-start", ",\"frames\":" + frames));
            Check(!request.Mutation && request.Integer("frames") == frames, "trace start bounds and no gameplay mutation");
        }
        Check(!TestControlProtocol.Parse(Request("board-trace-read")).Mutation, "trace read validation");
        foreach (string fields in new[] { "", ",\"frames\":0", ",\"frames\":-1", ",\"frames\":1201", ",\"frames\":1.5",
            ",\"frames\":\"2\"", ",\"frames\":[2]", ",\"frames\":2,\"extra\":1" })
        {
            bool rejected = false;
            try { TestControlProtocol.Parse(Request("board-trace-start", fields)); } catch (FormatException) { rejected = true; }
            Check(rejected, "invalid start: " + fields);
        }
        foreach (string fields in new[] { ",\"frames\":1", ",\"after\":0", ",\"matchId\":\"m\"" })
        {
            bool rejected = false;
            try { TestControlProtocol.Parse(Request("board-trace-read", fields)); } catch (FormatException) { rejected = true; }
            Check(rejected, "read rejects fields: " + fields);
        }

        var control = new NativeTestControl();
        Check(!(bool)control.ReadBoardTrace()["started"], "read before start");
        GameState.Current = new();
        var first = new Card { Entity = new Entity { Id = 10, Position = 1 } };
        var moving = new Card { Entity = new Entity { Id = 11, Position = 2 } };
        moving.Actor.transform.localPosition = new Vector3 { x = 8, y = 2, z = 3 };
        moving.Actor.transform.position = new Vector3 { x = 18, y = 12, z = 13 };
        GameState.Current.Friendly.Zone.Cards.AddRange(new[] { first, moving });
        GameState.Current.Enemy.Zone.Cards.Add(new Card { Entity = new Entity { Id = 20, Position = 1 }, Actor = null });
        Time.frameCount = 100; Time.realtimeSinceStartup = 5;
        var started = control.StartBoardTrace(2);
        Check((string)started["status"] == "running" && control.boardTrace.Samples.Count == 3 && !started.ContainsKey("samples"), "start samples immediately and returns status only");
        control.SampleBoardTrace();
        Check(control.boardTrace.Sampled == 1 && control.boardTrace.Samples.Count == 3, "once per frame");
        var before = (JsonNode)control.boardTrace.Samples[1];
        Check((int)before["slot"] == 1 && (int)before["zonePosition"] == 2 && (int)before["entityId"] == 11
            && (string)before["cardId"] == "TEST_CARD" && (int)before["frame"] == 100 && (float)before["realtime"] == 5
            && ((JsonList)before["localPosition"]).Cast<float>().SequenceEqual(new[] { 8f, 2f, 3f })
            && ((JsonList)before["worldPosition"]).Cast<float>().SequenceEqual(new[] { 18f, 12f, 13f }), "records identity, slot, tag, clock and both positions");
        var enemy = (JsonNode)control.boardTrace.Samples[2];
        Check((string)enemy["side"] == "enemy" && (int)enemy["player"] == 2 && enemy["localPosition"] == null, "enemy missing actor retained");
        GameState.Current.Friendly.Zone.Cards.Reverse();
        moving.Entity.Position = 1; moving.Actor.transform.localPosition.x = 3;
        Time.frameCount++; Time.realtimeSinceStartup += .016f;
        control.SampleBoardTrace();
        var after = (JsonNode)control.boardTrace.Samples[3];
        Check((string)control.ReadBoardTrace()["status"] == "done" && control.boardTrace.Sampled == 2
            && (int)after["slot"] == 0 && (int)after["zonePosition"] == 1 && (float)((JsonList)after["localPosition"])[0] == 3,
            "captures reordered moving actor and completes window");
        control.StartBoardTrace(1);
        Check(control.boardTrace.Samples.Count == 3 && control.runningBoardTrace == null, "restart replaces samples and one frame completes immediately");
        control.StartBoardTrace(1200);
        control.StopBoardTrace("disabled");
        Check(control.runningBoardTrace == null && control.boardTrace.Error == "disabled", "disable stops sampling and records reason");
        GameState.Current.Friendly.Zone.Fail = true;
        control.StartBoardTrace(2);
        Check(control.runningBoardTrace == null && control.boardTrace.Error.Contains("sample failure"), "sampling exception retained without escaping");
        GameState.Current = null;
        control.StartBoardTrace(2);
        Check(control.boardTrace.Error.Contains("GameState unavailable"), "missing game recorded");
        GameState.Current = new();
        for (int i = 0; i < 7; i++)
        {
            GameState.Current.Friendly.Zone.Cards.Add(new Card { Entity = new Entity { Id = i + 1, Position = i + 1 } });
            GameState.Current.Enemy.Zone.Cards.Add(new Card { Entity = new Entity { Id = i + 8, Position = i + 1 } });
        }
        control.StartBoardTrace(1200);
        while (control.runningBoardTrace != null) { Time.frameCount++; control.SampleBoardTrace(); }
        Check(control.boardTrace.Truncated && control.boardTrace.Samples.Count == BoardTraceMaxRecords, "real frame window hits record cap");
        Check(TestControlProtocol.Utf8.GetByteCount(Json.Serialize(control.ReadBoardTrace())) < TestControlProtocol.MaxResponse, "full response stays bounded");
        GameState.Current = new();
        GameState.Current.Friendly.Zone.Cards.Add(first);
        first.Entity.CardId = new string('x', BoardTraceMaxBytes);
        control.StartBoardTrace(1200);
        Check(control.boardTrace.Truncated && control.boardTrace.Samples.Count == 0, "byte cap rejects oversized row");
        Check(TestControlProtocol.Utf8.GetByteCount(Json.Serialize(control.ReadBoardTrace())) < TestControlProtocol.MaxResponse, "response stays bounded");
        Console.WriteLine($"PASS {checks} board trace and protocol checks");
    }
}

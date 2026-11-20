#if !SOLOSTONE_RELEASE
using System;
using MiniJSON;
using UnityEngine;

internal sealed partial class NativeTestControl
{
    private const int BoardTraceMaxRecords = 2400, BoardTraceMaxBytes = TestControlProtocol.MaxResponse - 16384;
    private sealed class BoardTrace
    {
        internal readonly JsonList Samples = new JsonList();
        internal int Requested, Sampled, LastFrame = -1, Bytes;
        internal bool Truncated;
        internal string Error;
    }
    private BoardTrace boardTrace, runningBoardTrace;

    private JsonNode StartBoardTrace(int frames)
    {
        AssertMainThread();
        boardTrace = runningBoardTrace = new BoardTrace { Requested = frames };
        SampleBoardTrace();
        return BoardTraceStatus();
    }

    private JsonNode BoardTraceStatus() => new JsonNode {
        { "started", boardTrace != null }, { "status", runningBoardTrace == null ? "done" : "running" },
        { "framesRequested", boardTrace?.Requested ?? 0 }, { "framesSampled", boardTrace?.Sampled ?? 0 },
        { "recordCount", boardTrace?.Samples.Count ?? 0 }, { "maxRecords", BoardTraceMaxRecords },
        { "truncated", boardTrace?.Truncated ?? false }, { "error", boardTrace?.Error }
    };

    private JsonNode ReadBoardTrace()
    {
        AssertMainThread();
        var result = BoardTraceStatus();
        result.Add("samples", boardTrace?.Samples ?? new JsonList());
        return result;
    }

    private void StopBoardTrace(string reason)
    {
        if (runningBoardTrace == null) return;
        runningBoardTrace.Error = reason.Length > 1024 ? reason.Substring(0, 1024) : reason;
        runningBoardTrace = null;
    }

    private void SampleBoardTrace()
    {
        // Test-control Update only; exceptions must never escape into Unity's frame loop.
        try
        {
            var trace = runningBoardTrace;
            int frame = Time.frameCount;
            if (trace.LastFrame == frame) return;
            trace.LastFrame = frame;
            float realtime = Time.realtimeSinceStartup;
            var front = GameState.Get();
            if (front == null) throw new InvalidOperationException("Board trace: GameState unavailable");
            SampleBoardTraceSide(front.GetFriendlySidePlayer(), "friendly", frame, realtime);
            if (runningBoardTrace == null) return;
            SampleBoardTraceSide(front.GetOpposingSidePlayer(), "enemy", frame, realtime);
            if (runningBoardTrace == null) return;
            trace.Sampled++;
            if (trace.Sampled >= trace.Requested) runningBoardTrace = null;
        }
        catch (Exception error) { StopBoardTrace(error.GetType().Name + ": " + error.GetBaseException().Message); }
    }

    private void SampleBoardTraceSide(Player player, string side, int frame, float realtime)
    {
        var cards = player?.GetBattlefieldZone()?.GetCards();
        if (cards == null) throw new InvalidOperationException("Board trace: " + side + " play zone unavailable");
        for (int index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var entity = card == null ? null : card.GetEntity();
            var actor = card == null ? null : card.GetActor();
            var row = new JsonNode {
                { "frame", frame }, { "realtime", realtime }, { "side", side }, { "player", player.GetPlayerId() },
                { "entityId", entity?.GetEntityId() ?? 0 }, { "cardId", entity?.GetCardId() },
                { "slot", index }, { "zonePosition", entity?.GetTag(GAME_TAG.ZONE_POSITION) ?? 0 },
                { "localPosition", actor == null ? null : BoardTracePosition(actor.transform.localPosition) },
                { "worldPosition", actor == null ? null : BoardTracePosition(actor.transform.position) }
            };
            var trace = runningBoardTrace;
            int bytes = TestControlProtocol.Utf8.GetByteCount(Json.Serialize(row)) + 1;
            if (trace.Samples.Count >= BoardTraceMaxRecords || trace.Bytes + bytes > BoardTraceMaxBytes)
            {
                trace.Truncated = true;
                runningBoardTrace = null;
                return;
            }
            trace.Samples.Add(row);
            trace.Bytes += bytes;
        }
    }

    private static JsonList BoardTracePosition(Vector3 position) => TestControlProtocol.List(new object[] { position.x, position.y, position.z });
}
#endif

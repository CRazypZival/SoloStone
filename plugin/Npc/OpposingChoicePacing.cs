using System;

// How long the NPC waits before it answers a choice the client shows as card backs (#162, #238).
// A decision battlecry such as Elise the Navigator answers in several steps, and the client runs the card's one shared play
// spell again for each step. If an answer lands while the spell of the step before is still alive, the client restarts the
// spell on top of itself and a fullscreen part of it never ends. So the NPC waits, in this order:
//  1. the deal: the client has dealt the backs. The wait counts only the time the client's power queue is idle (a busy
//     client cannot deal), at most DealWaitMilliseconds of it;
//  2. the source's play spell: it has ended, at most SpellWaitMilliseconds;
//  3. the beat: a human-plausible look at the dealt backs, counted from the deal.
// Steps 1 and 2 share one hard cap (WaitCapMilliseconds) from the first time the pick is ready, and end early when
// the turn's deadline is near. The beat is a floor: it does not come out of the runner's 4 s artificial-delay budget
// (NpcTurnRunner), and it never exceeds the beat itself, so every hold is bounded.
internal sealed class OpposingChoicePacing
{
    internal const double DealWaitMilliseconds = 3000;
    internal const double SpellWaitMilliseconds = 4000;
    internal const double WaitCapMilliseconds = 10000;
    // Steps 1 and 2 stop this long before the turn's own deadline.
    internal const double DeadlineMarginMilliseconds = 5000;

    private double started = -1, idleSince = -1, spellSince = -1;

    internal bool Started => started >= 0;

    // Milliseconds to keep holding the pick; 0 releases it. revealedAt < 0: not dealt yet. beat: the look at the backs.
    // sourceSpellActive may be null (nothing to probe) and may throw (counts as ended). deadlineLeft may be infinity.
    internal double Hold(double now, double revealedAt, double beat, bool powerIdle, Func<bool> sourceSpellActive, double deadlineLeft)
    {
        if (started < 0) started = now;
        bool waiting = now - started < WaitCapMilliseconds && deadlineLeft > DeadlineMarginMilliseconds;
        if (waiting && revealedAt < 0)
        {
            if (!powerIdle) { idleSince = -1; return 100; }
            if (idleSince < 0) idleSince = now;
            if (now - idleSince < DealWaitMilliseconds) return 100;
        }
        if (waiting && SpellActive(sourceSpellActive))
        {
            if (spellSince < 0) spellSince = now;
            if (now - spellSince < SpellWaitMilliseconds) return 100;
        }
        // The beat only follows a deal; an undealt choice that timed out goes at once, as before.
        return revealedAt < 0 ? 0 : Math.Max(0, beat - (now - revealedAt));
    }

    private static bool SpellActive(Func<bool> probe)
    {
        if (probe == null) return false;
        try { return probe(); } catch (Exception) { return false; }
    }
}

namespace Issun.Core;

/// <summary>relay.py's timing constants, shared by every module that needs them.</summary>
public static class Timing
{
    /// <summary>
    /// GAP_THRESHOLD: a silence longer than this is logged. The phone pushes
    /// every 30 s, so three missed heartbeats.
    /// </summary>
    public const double GapThreshold = 90.0;

    /// <summary>
    /// IDLE_TIMEOUT: clear presence if the phone stops reporting — covers
    /// force-quit and the app being evicted in the background.
    /// </summary>
    public const double IdleTimeout = 90.0;

    /// <summary>
    /// MIN_PUSH_GAP: Discord rate-limits activity updates, so rapid changes
    /// (skipping through a playlist) are coalesced rather than sent one per skip.
    /// </summary>
    public const double MinPushGap = 3.0;

    /// <summary>
    /// PLAYHEAD_SEEK_TOLERANCE: how far the reported position may move before it
    /// counts as a deliberate seek rather than a stale read.
    /// </summary>
    public const double PlayheadSeekTolerance = 10.0;

    /// <summary>
    /// PLAYHEAD_SETTLE_WINDOW: grace period after a track change during which any
    /// reading is accepted. currentPlaybackTime can still report the previous
    /// song for a second or two, and Ammy fires a correction push 2.5 s in —
    /// that correction has to be able to move the anchor backwards.
    /// </summary>
    public const double PlayheadSettleWindow = 6.0;
}

namespace Thor.ScanGate.Core;

/// <summary>What one evaluation of a scan concluded.</summary>
public enum ScanGateOutcome
{
    /// <summary>Every condition held; PAI was started (or already had been).</summary>
    Opened,

    /// <summary>
    /// Something is still in flight, and whatever finishes it will emit an event that evaluates
    /// the gate again — no need to hold on to this message.
    /// </summary>
    Waiting,

    /// <summary>
    /// Waiting only on an upload URL that has not expired yet. Nothing will emit an event if that
    /// upload never happens, so the message must come back later for the gate to look again.
    /// </summary>
    RecheckLater,
}

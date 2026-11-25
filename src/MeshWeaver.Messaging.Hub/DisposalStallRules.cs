namespace MeshWeaver.Messaging;

/// <summary>
/// The decisions of the disposal stall detector that are pure functions of what it measured, public
/// only so the test project can pin them without an assembly-wide <c>InternalsVisibleTo</c>
/// (applications never call this).
/// </summary>
public static class DisposalStallRules
{
    /// <summary>
    /// Whether a drain that is executing counts as teardown PROGRESS for the no-progress window
    /// (#6156): a drain body is in flight, NO turn is on the block (a turn on the block is judged by
    /// its own age), and a drain began since the detector's last look. The same drain in flight across
    /// the whole window, with nothing completing, is NOT progress — that is a stuck pump, and the
    /// verdicts must still fire for it.
    /// </summary>
    /// <param name="drainsInFlight">Drain bodies executing at this instant.</param>
    /// <param name="currentTurn">The message type on the block, or null when none.</param>
    /// <param name="drainsStartedNow">Drains begun, ever, now.</param>
    /// <param name="drainsStartedAtLastLook">The same counter at the previous verdict (or at Dispose()).</param>
    public static bool InFlightDrainIsProgress(
        int drainsInFlight, string? currentTurn, long drainsStartedNow, long drainsStartedAtLastLook)
        => drainsInFlight > 0
           && currentTurn is null
           && drainsStartedAtLastLook >= 0
           && drainsStartedNow > drainsStartedAtLastLook;
}

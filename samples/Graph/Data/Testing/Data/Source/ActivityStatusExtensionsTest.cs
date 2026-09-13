// <meshweaver>
// Id: Testing/Data/ActivityStatusExtensionsTest
// DisplayName: Testing/Data/ActivityStatusExtensionsTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Linq;
using MeshWeaver.Data;

/// <summary>
/// Pins the status-driven continuation + rendering law (<see cref="ActivityStatusExtensions"/>):
/// continuations fire on a transition into a follow-up (terminal) status; only a SUCCESS status
/// permits reading the real typed content; an ERROR status routes to emergency mode (render the
/// error). Pure-function tests — deterministic, no hub.
/// </summary>
public class ActivityStatusExtensionsTest
{
    [MeshTheory]
    [MeshInlineData(ActivityStatus.Running, false)]
    [MeshInlineData(ActivityStatus.Succeeded, true)]
    [MeshInlineData(ActivityStatus.Warning, true)]
    [MeshInlineData(ActivityStatus.Failed, true)]
    [MeshInlineData(ActivityStatus.Cancelled, true)]
    public void IsTerminal_IsEverythingButRunning(ActivityStatus status, bool expected)
        => Assert.Equal(expected, status.IsTerminal());

    [MeshTheory]
    [MeshInlineData(ActivityStatus.Succeeded, true)]
    [MeshInlineData(ActivityStatus.Warning, true)]
    [MeshInlineData(ActivityStatus.Running, false)]
    [MeshInlineData(ActivityStatus.Failed, false)]
    [MeshInlineData(ActivityStatus.Cancelled, false)]
    public void IsSuccess_OnlySucceededOrWarning(ActivityStatus status, bool expected)
        => Assert.Equal(expected, status.IsSuccess());

    [MeshTheory]
    [MeshInlineData(ActivityStatus.Failed, true)]
    [MeshInlineData(ActivityStatus.Cancelled, true)]
    [MeshInlineData(ActivityStatus.Running, false)]
    [MeshInlineData(ActivityStatus.Succeeded, false)]
    [MeshInlineData(ActivityStatus.Warning, false)]
    public void IsError_OnlyFailedOrCancelled(ActivityStatus status, bool expected)
        => Assert.Equal(expected, status.IsError());

    [MeshFact]
    public void FollowupStatuses_AreExactlyTheTerminalOnes()
    {
        var allTerminal = Enum.GetValues<ActivityStatus>().Where(s => s.IsTerminal()).ToHashSet();
        Assert.True(ActivityStatusExtensions.FollowupStatuses.SetEquals(allTerminal),
            "the follow-up set (what triggers a continuation) must be exactly the terminal statuses");
        Assert.DoesNotContain(ActivityStatus.Running, ActivityStatusExtensions.FollowupStatuses);
    }

    /// <summary>
    /// The core decision invariant: EVERY terminal status routes to exactly one branch —
    /// success (read the real typed content) XOR error (emergency mode). No terminal status is
    /// both or neither, so the "only on success do we type; otherwise emergency" rule is total.
    /// </summary>
    [MeshFact]
    public void EveryTerminalStatus_IsSuccessXorError()
    {
        foreach (var status in Enum.GetValues<ActivityStatus>().Where(s => s.IsTerminal()))
            Assert.True(status.IsSuccess() ^ status.IsError(),
                $"terminal status {status} must be exactly one of success / error (not both, not neither)");
    }

    [MeshFact]
    public void Running_IsNeitherSuccessNorError_SoNeverTypedNorEmergency()
    {
        Assert.False(ActivityStatus.Running.IsSuccess(), "a running activity must NOT have its content typed");
        Assert.False(ActivityStatus.Running.IsError(), "a running activity is not an error — it is still in progress");
    }
}

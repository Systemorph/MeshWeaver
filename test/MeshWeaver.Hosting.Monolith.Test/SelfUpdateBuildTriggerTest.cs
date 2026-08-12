using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Subjects;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the one distinction the self-update build trigger lives on
/// (<see cref="SelfUpdateHostedService.NewBuildEvents"/>): the replayed current state of the
/// <c>BuildCompletion</c> node — or its absence — is BASELINE, and only a version change observed
/// while watching is an event. Getting this wrong in the "replay" direction makes every pod start
/// look like a fresh green build; getting it wrong in the "absence" direction misses the first
/// build after the webhook is wired.
/// </summary>
public class SelfUpdateBuildTriggerTest
{
    private static MeshNode Node(long version) =>
        new("Systemorph.MeshWeaver", "Admin/_Build/Systemorph.MeshWeaver") { Version = version };

    private static (Subject<MeshNode?> Input, List<Unit> Events) Watch()
    {
        var input = new Subject<MeshNode?>();
        var events = new List<Unit>();
        SelfUpdateHostedService.NewBuildEvents(input).Subscribe(events.Add);
        return (input, events);
    }

    [Fact]
    public void ReplayedCurrentState_IsBaseline_NotAnEvent()
    {
        var (input, events) = Watch();
        input.OnNext(Node(5));
        Assert.Empty(events);

        input.OnNext(Node(6));
        Assert.Single(events);
    }

    [Fact]
    public void AbsentNode_ThenFirstWrite_IsAnEvent()
    {
        var (input, events) = Watch();
        input.OnNext(null);
        Assert.Empty(events);

        input.OnNext(Node(1));
        Assert.Single(events);
    }

    [Fact]
    public void UnchangedVersionReEmission_IsNotAnEvent()
    {
        var (input, events) = Watch();
        input.OnNext(Node(5));
        input.OnNext(Node(5));
        Assert.Empty(events);
    }

    [Fact]
    public void EverySubsequentBuild_IsOneEvent()
    {
        var (input, events) = Watch();
        input.OnNext(Node(5));
        input.OnNext(Node(6));
        input.OnNext(Node(7));
        input.OnNext(Node(8));
        Assert.Equal(3, events.Count);
    }
}

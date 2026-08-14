using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #1508 — the hub's lifecycle had no SOURCE.
///
/// <para><see cref="IMessageHub.RunLevel"/> is a plain property and
/// <see cref="IMessageHub.DisposalCompleted"/> fires at the very END of teardown, so anything
/// interested in the disposal WINDOW — the interval where the hub is
/// <see cref="MessageHubRunLevel.Quiescing"/>/<c>DisposeHostedHubs</c> but intake is still open —
/// had nothing to subscribe to: by the time the one available signal fired, the window had closed.
/// Every "wait for the hub to reach state X" therefore had to either SAMPLE the property on an
/// interval or contrive the ordering, and the sampling form is a standing violation of the reactive
/// rule. That window is where the interesting bugs live (#1470: a read serviced during teardown
/// answered as "the node does not exist").</para>
/// </summary>
public class HubRunLevelObservableTest
{
    private static IHost BuildHost(out IMessageHub mesh)
    {
        var hostBuilder = new HostBuilder();
        _ = new MeshHostBuilder(hostBuilder, new Address("mesh", "runlevel-source"));
        var host = hostBuilder.Build();
        mesh = host.Services.GetRequiredService<IMessageHub>();
        return host;
    }

    [Fact(Timeout = 60000)]
    public async Task ALateSubscriberIsToldTheLevelTheHubIsAlreadyIn()
    {
        using var host = BuildHost(out var mesh);
        await host.StartAsync();

        // Subscribing AFTER the hub started must not wait for the next transition — otherwise
        // subscribing in order to observe the disposal window would itself race the window, which
        // is the defect one level down.
        var current = await mesh.RunLevelChanged.FirstAsync().ToTask();

        current.Should().Be(mesh.RunLevel,
            "the source replays the current level, so a subscription is never behind the property");

        await host.StopAsync();
    }

    [Fact(Timeout = 60000)]
    public async Task TheDisposalWINDOWIsObservable_NotJustItsEnd()
    {
        using var host = BuildHost(out var mesh);
        await host.StartAsync();

        var seen = new List<MessageHubRunLevel>();
        using var subscription = mesh.RunLevelChanged.Subscribe(seen.Add);

        await host.StopAsync();

        // The end of teardown was always observable via DisposalCompleted. What was NOT is
        // everything before it — this is the whole point of the issue.
        seen.Should().Contain(MessageHubRunLevel.Dead, "the terminal level is still reported");
        seen.Should().Contain(
            l => l is MessageHubRunLevel.Quiescing
                   or MessageHubRunLevel.DisposeHostedHubs
                   or MessageHubRunLevel.ShutDown,
            "at least one INTERMEDIATE disposal phase must be observable — a source that only "
            + "reports the terminal state is DisposalCompleted with extra steps");
    }

    [Fact(Timeout = 60000)]
    public async Task TheSourceReportsTransitions_NotRepeatedAssignments()
    {
        using var host = BuildHost(out var mesh);
        await host.StartAsync();

        var seen = new List<MessageHubRunLevel>();
        using var subscription = mesh.RunLevelChanged.Subscribe(seen.Add);

        await host.StopAsync();

        // Several disposal arms assign the same terminal level defensively (the Dead backstop in
        // the finally, for one). A subscriber should see the lifecycle, not how many times a field
        // was written.
        seen.Should().OnlyHaveUniqueItems(
            "the source publishes TRANSITIONS; a repeated assignment of the level the hub is "
            + "already in is not a lifecycle event");
    }

    [Fact(Timeout = 60000)]
    public async Task TheSourceCOMPLETESAtDead()
    {
        using var host = BuildHost(out var mesh);
        await host.StartAsync();

        var completed = false;
        using var subscription = mesh.RunLevelChanged.Subscribe(_ => { }, () => completed = true);

        await host.StopAsync();

        completed.Should().BeTrue(
            "Dead is terminal, so the source must complete — otherwise every completion-shaped "
            + "composition over it (LastAsync, ToTask) hangs on a hub that will never emit again");
    }
}

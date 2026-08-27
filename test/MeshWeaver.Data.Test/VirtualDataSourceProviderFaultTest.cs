using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>A row produced by the faulting virtual provider below.</summary>
/// <param name="Id">Key.</param>
/// <param name="Name">Payload.</param>
public record FaultingRow([property: Key] int Id, string Name);

/// <summary>
/// Pins the ERROR ARM on <c>VirtualDataSource</c>'s live-update subscription
/// (Systemorph/MeshWeaver#2468).
///
/// <para>A virtual type's provider is arbitrary composed content — a mesh read, a query hop, a
/// <c>CombineLatest</c> over other streams — so it CAN fault. The subscription that pushes its
/// later emissions into the data source used to be a one-argument <c>Subscribe(onNext)</c>, and
/// Rx's default <c>onError</c> for that overload is <c>Stubs.Throw</c>: it RETHROWS the fault on
/// whatever thread carried it.</para>
///
/// <para>That thread is almost never one with a catch. In #2468 the provider's
/// <c>hub.GetMeshNode(...)</c> timed out, so the <c>OnError</c> originated inside a
/// <c>CancellationTokenSource</c> callback on a <c>TimerQueue</c> thread; the rethrow was an
/// UNHANDLED exception, the host aborted (core dumped), and the Doc content gate reported
/// <i>"failed before it produced a verdict — no check was judged"</i>. A gate that dies before
/// judging is worse than a gate that fails, so this is pinned rather than left to review.</para>
///
/// <para>The test drives the fault from the TEST thread precisely because the rethrow is
/// synchronous on the emitting thread — that is what makes it observable here at all, and it is
/// the same rethrow that has no catch in production.</para>
/// </summary>
public class VirtualDataSourceProviderFaultTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly BehaviorSubject<IEnumerable<FaultingRow>> rows =
        new([new FaultingRow(1, "seed")]);

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data
                .WithVirtualDataSource("Faulting", vds =>
                    vds.WithVirtualType<FaultingRow>(_ => rows)));

    /// <summary>
    /// A faulting provider must be REPORTED by the data source, never rethrown onto the thread
    /// that produced the fault.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProviderFault_IsReported_NeverRethrownOnTheEmittingThread()
    {
        var workspace = GetHost().GetWorkspace();

        // Let the source come up first, so the live-update subscription — the one under test — is
        // the subscriber that is still attached when the fault lands (initialization's Take(1) has
        // already released).
        var stream = workspace.GetStream(typeof(FaultingRow));
        await stream.Should().Within(TimeSpan.FromSeconds(10)).Emit();

        var fault = new TimeoutException(
            "GetMeshNode('$model-probe/deadbeef') timed out after 10.0s — the shape of #2468");

        var act = () => rows.OnError(fault);

        act.Should().NotThrow(
            "a virtual provider's fault must be reported by the data source, not rethrown onto "
            + "the emitting thread — in production that thread is a CTS TimerQueue thread with "
            + "no catch anywhere above it, so the rethrow aborts the process");
    }
}

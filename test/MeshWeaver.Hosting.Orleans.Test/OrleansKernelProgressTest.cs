using System;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end coverage of a script's logger output propagating through the
/// <c>ActivityLog</c> stream. A script posted via <see cref="SubmitCodeRequest"/>
/// calls <c>Log.LogInformation("...")</c>; subscribers to the activity log's
/// <c>MeshNodeReference</c> stream see each message land — same shape as Thread
/// streams.
///
/// Replaces the earlier <c>IProgress&lt;string&gt; Progress</c>-based tests; that
/// API was removed in favour of ActivityLog streaming. Tests are Skip-marked
/// until the ActivityLog plumbing in task #60 lands (the kernel needs to create
/// an ActivityLog MeshNode at script dispatch and thread its id/logger through
/// to the script's <c>Log</c> global).
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansKernelProgressTest(SharedOrleansFixture fixture, ITestOutputHelper output) : OrleansSharedTestBase(fixture, output)
{
    private const int DefaultTimeoutMs = 30_000;

    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"kernel-{name}-{Guid.NewGuid():N}", "TestUser");

    [Fact(Timeout = DefaultTimeoutMs, Skip = "Pending task #60: ActivityLog created at kernel dispatch + Log global wired through")]
    public async Task Log_from_script_is_observable_on_activity_log_stream()
    {
        var client = await GetClientAsync();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        const string code =
            """
            Log.LogInformation("step-one");
            Log.LogInformation("step-two");
            """;

        client.Post(
            new SubmitCodeRequest(code) { Id = Guid.NewGuid().ToString("N") },
            o => o.WithTarget(kernelAddress));

        // Once #60 is wired, the ExecuteScriptResponse carries the ActivityLog
        // path. Subscribe via GetRemoteStream<MeshNode, MeshNodeReference> on
        // that path; assert the messages show up as ActivityLog.Messages entries.
        await Task.CompletedTask;
    }

    [Fact(Timeout = DefaultTimeoutMs, Skip = "Pending task #60")]
    public async Task Log_survives_exceptions_inside_script()
    {
        // Contract: Log must be best-effort. If a subsequent line in the script
        // throws, earlier log entries still landed on the ActivityLog.
        await Task.CompletedTask;
    }

    [Fact(Timeout = DefaultTimeoutMs, Skip = "Pending task #60")]
    public async Task Each_submission_has_its_own_activity_log()
    {
        // Each SubmitCodeRequest gets a fresh ActivityLog node. Submission 1's
        // messages don't bleed into submission 2's stream.
        await Task.CompletedTask;
    }
}

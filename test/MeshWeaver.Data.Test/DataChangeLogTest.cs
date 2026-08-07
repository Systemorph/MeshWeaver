using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>Test entity whose <c>Name</c> is required, so a null makes the change fail validation.</summary>
/// <param name="Id">The key.</param>
/// <param name="Name">Required — a null value fails DataAnnotations validation.</param>
public record ChangeLogRecord(string Id, [property: Required] string? Name);

/// <summary>
/// Pins the contract of the reactive data change (<see cref="WorkspaceOperations.Change"/>):
/// the write is issued eagerly, the returned observable reports the outcome exactly once, the
/// <see cref="ActivityLog"/> carries the validation failures — and no <see cref="Activity"/> hub
/// is spun up per change.
///
/// <para>What this replaced: every <c>DataChangeRequest</c> used to construct an
/// <see cref="Activity"/>, which hosts a HUB (plus one more per data source, via
/// <c>StartSubActivity</c>), purely to latch "all streams applied" and accumulate messages.</para>
/// </summary>
public class DataChangeLogTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source =>
                source.WithType<ChangeLogRecord>(type => type
                    .WithKey(instance => instance.Id)
                    .WithInitialData(_ => Observable.Return<IEnumerable<ChangeLogRecord>>(
                        [new ChangeLogRecord("1", "First")])))));

    /// <summary>
    /// Returns the workspace only once its data source's stream hub has actually STARTED.
    ///
    /// <para>🚨 <c>WorkspaceOperations.UpdateStream</c> hard-throws
    /// <c>"Data source … is not initialized."</c> when a change arrives before
    /// <c>stream.Hub.Started</c> has completed. Writing straight after
    /// <c>GetHost().GetWorkspace()</c> is therefore a race against hub startup: it wins on a
    /// developer machine (the whole class runs in ~370 ms) and loses on a loaded CI runner,
    /// where it surfaced as a one-test failure that looked like an unrelated flake.</para>
    ///
    /// <para>The seeded record <c>"1"</c> is the readiness signal — observing it proves the
    /// source is up. We wait on that ACTUAL condition rather than sleeping (WritingTests.md).</para>
    /// </summary>
    private async Task<IWorkspace> GetStartedWorkspace()
    {
        var workspace = GetHost().GetWorkspace();
        await workspace.GetObservable<ChangeLogRecord>()
            .Should().Within(10.Seconds()).Match(x => x.Any(r => r.Id == "1"));
        return workspace;
    }

    [Fact]
    public async Task Change_ReportsOnce_ThenCompletes()
    {
        var workspace = await GetStartedWorkspace();

        var notifications = await workspace
            .RequestChange(DataChangeRequest.Update([new ChangeLogRecord("2", "Second")]))
            .Materialize()
            .ToArray()
            .Timeout(10.Seconds())
            .ToTask();

        notifications.Select(n => n.Kind).Should()
            .Equal([NotificationKind.OnNext, NotificationKind.OnCompleted],
                "the change reports its log exactly once, then completes");
        var log = notifications[0].Value;
        log.Status.Should().Be(ActivityStatus.Succeeded);
        log.End.Should().NotBeNull("the log is finished, not left running");

        var records = await workspace.GetObservable<ChangeLogRecord>()
            .Should().Within(5.Seconds()).Match(x => x.Any(r => r.Id == "2"));
        records.Should().Contain(r => r.Id == "2" && r.Name == "Second");
    }

    [Fact]
    public async Task Change_WhenValidationFails_ReportsTheFailure_AndDoesNotWrite()
    {
        var workspace = await GetStartedWorkspace();

        var log = await workspace
            .RequestChange(DataChangeRequest.Update([new ChangeLogRecord("3", null)]))
            .Timeout(10.Seconds())
            .ToTask();

        log.Status.Should().Be(ActivityStatus.Failed);
        log.Messages.Should().Contain(m =>
            m.LogLevel == LogLevel.Error && m.Message.Contains("Name") && m.Message.Contains("invalid"));

        var records = await workspace.GetObservable<ChangeLogRecord>()
            .FirstAsync().Timeout(5.Seconds()).ToTask();
        records.Should().NotContain(r => r.Id == "3", "a change that fails validation is not applied");
    }

    [Fact]
    public async Task DataChangeRequest_DoesNotHostAnActivityHub()
    {
        var host = GetHost();
        // Same startup race as above: the change below reaches the very same UpdateStream guard,
        // just via the client hub instead of directly.
        await GetStartedWorkspace();
        var hostedHubs = host.ServiceProvider.GetRequiredService<HostedHubsCollection>();
        int ActivityHubCount() => hostedHubs.Hubs.Count(h => h.Address.Type == AddressExtensions.ActivityType);

        var before = ActivityHubCount();

        var response = await GetClient()
            .Observe(new DataChangeRequest { Updates = [new ChangeLogRecord("4", "Fourth")] },
                o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit();

        response.Message.Should().BeOfType<DataChangeResponse>()
            .Which.Status.Should().Be(DataChangeStatus.Committed);
        ActivityHubCount().Should().Be(before,
            "a data change reports through its observable — it does not host an Activity hub to latch completion");
    }
}

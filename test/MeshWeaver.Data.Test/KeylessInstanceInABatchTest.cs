using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>Test entity whose key may be absent, so one instance of a batch can carry no key value.</summary>
/// <param name="Id">The key — null on the instance the test means to be skipped.</param>
/// <param name="Name">A payload field.</param>
public record KeylessBatchRecord(string? Id, string Name);

/// <summary>
/// A bulk update that carries one instance with no key value reports and SKIPS that instance and
/// applies the rest of the batch (<c>WorkspaceOperations.UpdateDataChangeRequest</c>).
///
/// <para>The defect this pins: the update filtered on <c>TypeDefinition.GetKey(x) == null</c>,
/// but <c>GetKey</c> throws for a null key value instead of returning null. So the filter could
/// never select anything — the throw escaped into the update's catch, which faulted the whole
/// data stream: the keyed instances of the batch were lost with the keyless one, and every later
/// write to the collection failed too.</para>
/// </summary>
public class KeylessInstanceInABatchTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source =>
                source.WithType<KeylessBatchRecord>(type => type
                    .WithKey(instance => instance.Id!)
                    .WithInitialData(_ => Observable.Return<IEnumerable<KeylessBatchRecord>>(
                        [new KeylessBatchRecord("seed", "Seed")])))));

    // The seeded record proves the data source has started (a change before that hard-throws).
    private async Task<IWorkspace> GetStartedWorkspace()
    {
        var workspace = GetHost().GetWorkspace();
        await workspace.GetObservable<KeylessBatchRecord>()
            .Should().Within(10.Seconds()).Match(x => x.Any(r => r.Id == "seed"),
                cancellationToken: TestContext.Current.CancellationToken);
        return workspace;
    }

    /// <summary>
    /// The keyless instance is reported under its catalog key and skipped; the keyed one is
    /// applied; and the stream survives — a later write to the same collection still lands.
    /// </summary>
    [Fact]
    public async Task KeylessInstance_IsReportedAndSkipped_TheRestOfTheBatchApplies()
    {
        var workspace = await GetStartedWorkspace();

        var log = await workspace
            .RequestChange(DataChangeRequest.Update(
                [new KeylessBatchRecord("kept", "Kept"), new KeylessBatchRecord(null, "Keyless")]))
            .Timeout(10.Seconds())
            .Await();

        log.Messages.Should().Contain(m =>
                m.LogLevel == LogLevel.Error && m.MessageKey == "activity.dataUpdate.nullKeyInstancesSkipped",
            "the keyless instance is reported as skipped, not turned into a stream fault");
        log.Messages.Should().NotContain(m => m.MessageKey == "activity.dataUpdate.streamError",
            "one keyless instance must not fault the data stream");

        await workspace.GetObservable<KeylessBatchRecord>()
            .Should().Within(5.Seconds()).Match(x => x.Any(r => r.Id == "kept"),
                cancellationToken: TestContext.Current.CancellationToken);

        // The stream is still alive: a later, ordinary write applies.
        await workspace
            .RequestChange(DataChangeRequest.Update([new KeylessBatchRecord("after", "After")]))
            .Timeout(10.Seconds())
            .Await();
        await workspace.GetObservable<KeylessBatchRecord>()
            .Should().Within(5.Seconds()).Match(x => x.Any(r => r.Id == "after"),
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Negative control: a batch where every instance carries a key reports nothing skipped.
    /// </summary>
    [Fact]
    public async Task FullyKeyedBatch_ReportsNothingSkipped()
    {
        var workspace = await GetStartedWorkspace();

        var log = await workspace
            .RequestChange(DataChangeRequest.Update([new KeylessBatchRecord("k1", "One"), new KeylessBatchRecord("k2", "Two")]))
            .Timeout(10.Seconds())
            .Await();

        log.Status.Should().Be(ActivityStatus.Succeeded);
        log.Messages.Should().NotContain(m => m.MessageKey == "activity.dataUpdate.nullKeyInstancesSkipped");
    }
}

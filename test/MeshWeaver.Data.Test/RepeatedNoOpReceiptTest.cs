using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Pins the owner-side causal receipt used by unified data writes. A synchronization stream adopts
/// a value-equal patch's version without publishing it, so the receipt must retain the last frame a
/// reduced read could actually have received across any number of consecutive no-ops.
/// </summary>
public class RepeatedNoOpReceiptTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source => source
                .WithType<BusinessUnit>(type => type.WithInitialData(TestData.BusinessUnits))));

    [HubFact]
    public async Task ASecondNoOpRetainsTheLastPublishedOwnerVersion()
    {
        var workspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var read = workspace.GetNullableStream(new EntityReference(nameof(BusinessUnit), "1"))!;
        var initial = await read.Should().Within(TestTimeouts.Convergence)
            .Match(frame => frame.Value is BusinessUnit);
        var desired = (BusinessUnit)initial.Value!;
        var source = workspace.DataContext.DataSourcesByCollection[nameof(BusinessUnit)]
            .GetStreamForPartition(null)!;
        var originallyPublished = await source.Take(1).Should().Within(TestTimeouts.Quick).Emit();

        var first = await workspace.ChangeWithReceipt(DataChangeRequest.Update([desired]))
            .Should().Within(TestTimeouts.Quick).Emit("the first idempotent update still commits");
        first.VisibleVersions.Should().ContainSingle().Which.Should().Be(originallyPublished.Version);
        source.Current!.Version.Should().BeGreaterThan(originallyPublished.Version,
            "the owner silently advances even though the value-equal patch was not published");

        var second = await workspace.ChangeWithReceipt(DataChangeRequest.Update([desired]))
            .Should().Within(TestTimeouts.Quick).Emit("the second idempotent update also commits");
        var lastPublished = await source.Take(1).Should().Within(TestTimeouts.Quick).Emit();
        lastPublished.Version.Should().Be(originallyPublished.Version,
            "neither no-op emitted a replacement owner frame");
        read.Current!.Version.Should().Be(originallyPublished.Version,
            "the read has no later published owner frame to consume");
        Output.WriteLine(
            $"DIAG: first receipt={first.VisibleVersions[0]}, second receipt={second.VisibleVersions[0]}, "
            + $"owner Current={source.Current!.Version}, last owner OnNext={lastPublished.Version}, "
            + $"read={read.Current.Version}.");
        second.VisibleVersions.Should().ContainSingle().Which.Should().Be(lastPublished.Version,
            "the previous Current version can itself be an unpublished no-op; the receipt must "
            + "reference a frame reduced reads can actually receive");
    }
}

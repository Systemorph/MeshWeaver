#pragma warning disable CS1591

using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins the wire contract between the tester and the combo verifier: <c>mw-plugin-test --report</c>
/// serializes <see cref="GateReport.ToRunReport"/> with <see cref="InstanceComboAssembler.Json"/>,
/// and <c>mw-combo-verify</c> — running OUTSIDE the candidate image, possibly built from a
/// different commit — deserializes the same bytes back into <see cref="GateRunReport"/>. Everything
/// the verdict folding depends on must survive the round-trip: identities, every check outcome,
/// every failure detail, and the fatal error.
/// </summary>
public class GateRunReportContractTest
{
    [Fact]
    public void EveryVerdictBearingField_SurvivesTheSerializedRoundTrip()
    {
        var report = new GateReport(
        [
            new PackageResult("Widget")
            {
                NodeCount = 3,
                NodeTypes =
                [
                    new NodeTypeResult("Widget/Thing", "Widget")
                    {
                        CompilationStatus = CompilationStatus.Error,
                        Compile = CheckOutcome.Failed,
                        CompileDetail = "CS0117: no 'AddTracking'",
                        Render = CheckOutcome.Skipped,
                        Tests = CheckOutcome.Skipped,
                    },
                    new NodeTypeResult("Widget/Other", "Widget")
                    {
                        CompilationStatus = CompilationStatus.Ok,
                        Compile = CheckOutcome.Passed,
                        Render = CheckOutcome.Passed,
                        Tests = CheckOutcome.Failed,
                        TestsDetail = "1 red row",
                    },
                ],
            },
            new PackageResult("Store")
            {
                NodeCount = 1,
                InstallError = "import faulted",
                IdempotenceError = "second install wrote 2 node(s)",
            },
        ]);

        var json = JsonSerializer.Serialize(report.ToRunReport(), InstanceComboAssembler.Json);
        var read = JsonSerializer.Deserialize<GateRunReport>(json, InstanceComboAssembler.Json)!;

        read.FatalError.Should().BeNull();
        read.Packages.Should().HaveCount(2);

        var widget = read.Packages.Single(p => p.Id == "Widget");
        widget.NodeCount.Should().Be(3);
        widget.Success.Should().BeFalse();
        var thing = widget.NodeTypes.Single(t => t.Path == "Widget/Thing");
        thing.CompilationStatus.Should().Be(nameof(CompilationStatus.Error));
        thing.Compile.Should().Be(GateRunOutcome.Failed);
        thing.CompileDetail.Should().Be("CS0117: no 'AddTracking'");
        thing.Render.Should().Be(GateRunOutcome.Skipped);
        var other = widget.NodeTypes.Single(t => t.Path == "Widget/Other");
        other.Compile.Should().Be(GateRunOutcome.Passed);
        other.Tests.Should().Be(GateRunOutcome.Failed);
        other.TestsDetail.Should().Be("1 red row");

        var store = read.Packages.Single(p => p.Id == "Store");
        store.InstallError.Should().Be("import faulted");
        store.IdempotenceError.Should().Be("second install wrote 2 node(s)");
        store.Success.Should().BeFalse();
    }

    [Fact]
    public void FatalError_AndAllGreen_RoundTripToo()
    {
        var fatal = new GateReport([]) { FatalError = "mesh boot failed" };
        var readFatal = Roundtrip(fatal);
        readFatal.FatalError.Should().Be("mesh boot failed");
        readFatal.Packages.Should().BeEmpty();

        var green = new GateReport(
        [
            new PackageResult("Widget")
            {
                NodeCount = 1,
                NodeTypes =
                [
                    new NodeTypeResult("Widget/Thing", "Widget")
                    {
                        CompilationStatus = CompilationStatus.Ok,
                        Compile = CheckOutcome.Passed,
                        Render = CheckOutcome.Passed,
                        Tests = CheckOutcome.Passed,
                    },
                ],
            },
        ]);
        var readGreen = Roundtrip(green);
        readGreen.Packages.Single().Success.Should().BeTrue();
        readGreen.Packages.Single().NodeTypes.Single().Success.Should().BeTrue();
    }

    private static GateRunReport Roundtrip(GateReport report) =>
        JsonSerializer.Deserialize<GateRunReport>(
            JsonSerializer.Serialize(report.ToRunReport(), InstanceComboAssembler.Json),
            InstanceComboAssembler.Json)!;
}

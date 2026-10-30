using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;
using static MeshWeaver.Graph.Test.MarkdownEditIsATemplateTest;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#7356 — a release request answered by a delivery hold must say so on the NodeType page.
///
/// <para><b>What it was.</b> The unknown-membership hold (<see cref="BuildDeliveryHold.SettleUnknownMembership"/>)
/// keeps the last build serving as <c>Ok</c> with <see cref="BuildProvenance.Compiled"/> and puts its
/// reason in <see cref="NodeTypeDefinition.DeliveryHoldReason"/> only. The page derived its words from
/// the status and the provenance, so it read "Up to date" and never showed the reason: a forced release
/// the hold had answered with no build looked successful.</para>
///
/// <para><b>What the arms pin.</b> (1) The status projection carries the reason, with a held chip, for a
/// record the real settle produced, and hides the block for a plain <c>Ok</c> record (negative control).
/// (2) The hold block is a template whose pointers name real projection members, and it is on the
/// Overview and the Configuration pane. (3) Rendered for real, the Overview area publishes the reason
/// where the block points.</para>
///
/// <para>Negative control, run before this file was committed: against the code without the hold block
/// the projection has no <c>HoldDetail</c> member, so arms (1) and (3) do not compile; with the
/// <c>DeliveryHold</c> decision returning the hidden shape, arms (1) and (3) fail on the missing reason.</para>
/// </summary>
public class ADeliveryHoldIsShownOnTheNodeTypePageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "acme/Widget";

    private const string Reason =
        "Unknown module membership: prebuilt-bundle discovery was incomplete (test reason 7356).";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>The record the compile watcher writes for an unknown-membership hold — produced by the
    /// real settle, not hand-assembled, so the test follows that settle if it changes.</summary>
    private static NodeTypeDefinition UnknownMembershipHeld(string? configuration = "config => config")
        => BuildDeliveryHold.SettleUnknownMembership(
            new NodeTypeDefinition
            {
                Configuration = configuration,
                CompilationStatus = CompilationStatus.Pending,
                BuildProvenance = BuildProvenance.Compiled,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = "Widget.dll",
            },
            hasUsableBuild: true,
            Reason)!;

    private static NodeTypeDefinition PlainOk(string? configuration = "config => config") => new()
    {
        Configuration = configuration,
        CompilationStatus = CompilationStatus.Ok,
        BuildProvenance = BuildProvenance.Compiled,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Widget.dll",
    };

    [Fact]
    public void TheProjection_CarriesTheUnknownMembershipReason_AndAPlainOkRecordShowsNoHold()
    {
        var held = UnknownMembershipHeld();
        held.CompilationStatus.Should().Be(CompilationStatus.Ok, "the hold keeps the last build serving");
        held.BuildProvenance.Should().Be(BuildProvenance.Compiled,
            "the hold keeps its provenance — which is exactly why a provenance-keyed page missed it");
        BuildDeliveryHold.IsHeld(held).Should().BeFalse(
            "IsHeld is the notifier's provenance test and is deliberately not widened (#7356)");

        var view = NodeTypeStatusView.From(new MeshNode("Widget", "acme"), held, TypePath, "en");
        view.HoldStyle.Should().NotContain("display: none", "a held record shows the hold block");
        view.HoldHeadline.Should().Be("Serving the last build — delivery hold");
        view.HoldDetail.Should().Contain(Reason, "the recorded reason is rendered verbatim");
        view.PanelChip.Should().Be("Serving the last build — delivery hold",
            "the Overview chip must not read 'Up to date' over a release the hold answered");

        var german = NodeTypeStatusView.From(new MeshNode("Widget", "acme"), held, TypePath, "de");
        german.HoldHeadline.Should().Be("Letzter Build wird ausgeliefert — Auslieferung angehalten",
            "the chrome is the viewer's language");
        german.HoldDetail.Should().Contain(Reason, "the reason is mesh-written data and stays as written");

        var ok = NodeTypeStatusView.From(new MeshNode("Widget", "acme"), PlainOk(), TypePath, "en");
        ok.HoldStyle.Should().Contain("display: none", "a record that is not held shows no hold block");
        ok.HoldHeadline.Should().BeEmpty();
        ok.HoldDetail.Should().BeEmpty();
        ok.PanelChip.Should().Be("Up to date");

        var refused = NodeTypeStatusView.From(null, PlainOk() with
        {
            CompilationStatus = CompilationStatus.Unavailable,
            BuildProvenance = BuildProvenance.AdoptionRefused,
            DeliveryHoldReason = "Incompatible build, awaiting bundle.",
        }, TypePath, "en");
        refused.HoldHeadline.Should().Be("Delivery hold: the last release produced no new build",
            "any record carrying a reason shows it — a refused build is not 'serving'");
        refused.HoldDetail.Should().Contain("Incompatible build, awaiting bundle.");
    }

    [Fact]
    public void TheHoldBlock_IsATemplate_OnTheOverviewAndTheConfigurationPane()
    {
        var block = NodeTypeLayoutAreas.BuildDeliveryHoldTemplate();
        EveryViewIsStatic(block);

        var members = typeof(NodeTypeStatusView).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);
        var pointers = Descendants(block)
            .Where(c => c.DataContext == NodeTypeStatusView.DataContext)
            .SelectMany(c => new[] { c.Style, (c as LabelControl)?.Data, (c as MarkdownControl)?.Markdown })
            .OfType<JsonPointerReference>()
            .Select(p => p.Pointer)
            .ToList();
        string.Join(",", pointers.OrderBy(p => p, StringComparer.Ordinal))
            .Should().Be("holdDetail,holdHeadline,holdStyle", "the block binds its style, headline and detail");
        pointers.Should().OnlyContain(p => members.Contains(p), "an unknown pointer draws empty with no error");

        Descendants(NodeTypeLayoutAreas.BuildConfigurationTemplate(TypePath, "en"))
            .Any(c => c.Style is JsonPointerReference { Pointer: "holdStyle" })
            .Should().BeTrue("the Configuration pane carries the hold block");
    }

    [Fact]
    public async Task TheOverview_RendersTheReason_ForAnUnknownMembershipHold()
    {
        var held = await CreateType("held-type-", UnknownMembershipHeld(configuration: null));
        var plain = await CreateType("plain-type-", PlainOk(configuration: null));

        await Field(held, nameof(NodeTypeStatusView.HoldDetail)).Should().Within(TestTimeouts.Convergence).Match(
            d => d != null && d.Contains(Reason),
            "the Overview publishes the recorded reason where the hold block points");

        var plainStyle = Field(plain, nameof(NodeTypeStatusView.HoldStyle));
        await plainStyle.Should().Within(TestTimeouts.Convergence).Match(
            s => s != null && s.Contains("display: none"),
            "negative control: a plain Ok type renders no hold block");
        await Field(plain, nameof(NodeTypeStatusView.HoldDetail)).Should().Within(TestTimeouts.Convergence).Match(
            d => d == "", "negative control: no reason for a type that is not held");
    }

    private async Task<string> CreateType(string prefix, NodeTypeDefinition definition)
    {
        var id = prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = definition,
        }).Should().Within(TestTimeouts.Convergence).Emit();
        return $"{TestPartition}/{id}";
    }

    private IObservable<string?> Field(string path, string member)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), new LayoutAreaReference(NodeTypeLayoutAreas.OverviewArea));
        return stream.GetDataStream<string>(new JsonPointerReference(
            NodeTypeStatusView.DataContext + "/" + NodeTypeStatusView.Pointer(member).Pointer));
    }
}

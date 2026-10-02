using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
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
/// "Templates first, data later" (Doc/GUI/DataBinding) for the NodeType and Settings pages.
///
/// <para><b>What they were.</b> The NodeType Configuration, Releases and HubConfig panes and the
/// Overview compile panel were built only once the NodeType node had arrived — the title, the
/// configuration lambda, every status line and the release rows (hand-built HTML) interpolated from
/// it — and the Settings Groups and Admin Data Sources tabs waited on a hub-side query and drew one
/// card per loaded node. Until the slowest read answered, those panes were a spinner.</para>
///
/// <para><b>What the arms pin.</b> (1) Each pane is a TEMPLATE: every view is a control, so its
/// first emission waits on nothing. (2) Every control bound to the status projection points at a
/// member that EXISTS on <see cref="NodeTypeStatusView"/> — a misspelt pointer would draw empty
/// forever, silently. (3) The decisions the panes show are made by the pure
/// <see cref="NodeTypeStatusView.From"/>. (4) Rendered for real, the projection reaches the
/// template's pointers and FOLLOWS a later edit of the node.</para>
///
/// <para>Negative control, run before this file was committed: against the pre-conversion code the
/// builders did not exist as host-free templates — the panes took a loaded <see cref="MeshNode"/>
/// (<c>BuildConfigurationPane(host, address, node)</c>) and were reached only through
/// <c>GetNodeStream(host).Select(…)</c>, the deferred view <see cref="EveryViewIsStatic"/> rejects.
/// With a <see cref="NodeTypeStatusView.Pointer"/> renamed to a non-member, arm (2) fails.</para>
/// </summary>
public class NodeTypeAndSettingsPagesAreTemplatesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "acme/Widget";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    public static TheoryData<string> Panes => ["Configuration", "Releases", "HubConfig", "CompilePanel"];

    private static UiControl Pane(string name) => name switch
    {
        "Configuration" => NodeTypeLayoutAreas.BuildConfigurationTemplate(TypePath, "en"),
        "Releases" => NodeTypeLayoutAreas.BuildReleasesTemplate(TypePath, "en"),
        "HubConfig" => NodeTypeLayoutAreas.BuildHubConfigViewTemplate(TypePath, "en"),
        "CompilePanel" => NodeTypeLayoutAreas.BuildCompileStatusPanel(TypePath),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Panes))]
    public void TheNodeTypePane_IsATemplate_WhosePointersNameRealMembers(string pane)
    {
        var template = Pane(pane);

        EveryViewIsStatic(template);

        var members = typeof(NodeTypeStatusView).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);
        var bound = Descendants(template)
            .Where(c => c.DataContext == NodeTypeStatusView.DataContext)
            .SelectMany(c => PointersOf(c).Select(p => (Control: c.GetType().Name, p.Pointer)))
            .ToList();

        bound.Should().NotBeEmpty($"the {pane} pane shows the compile state, so it binds to the status projection");
        bound.Where(b => !members.Contains(b.Pointer)).Should().BeEmpty(
            "every pointer into the projection must name a member it serializes — an unknown pointer "
            + "resolves to nothing and the control draws empty with no error");
    }

    [Fact]
    public void TheConfigurationForm_BindsTheNodeItself()
    {
        var fields = Descendants(NodeTypeLayoutAreas.BuildConfigurationTemplate(TypePath, "en"))
            .Where(c => c is TextFieldControl or TextAreaControl)
            .ToList();

        fields.Should().HaveCount(7, "Name, Icon, ChildrenQuery, DefaultNamespace, PageMaxWidth, Description, ReleaseNotes");
        fields.Select(f => LayoutAreaReference.TryParseMeshNodeDataContext(f.DataContext))
            .Should().AllSatisfy(ctx =>
            {
                ctx.Should().NotBeNull("the form edits the node directly — never a /data replica");
                ctx!.Value.NodePath.Should().Be(TypePath);
            });
    }

    [Fact]
    public void TheListsAreRunByTheGui()
    {
        var releases = Descendants(NodeTypeLayoutAreas.BuildReleasesTemplate(TypePath, "en"))
            .OfType<MeshSearchControl>().Should().ContainSingle(
                "the release history is a query control — the hub no longer loads the releases").Subject;
        ((string?)releases.HiddenQuery).Should().Be(
            $"namespace:{TypePath}/{ReleaseNodeType.ReleaseSegment} nodeType:{ReleaseNodeType.NodeType} sort:CreatedAt-desc");

        ((string?)SettingsLayoutArea.GroupsList("acme").HiddenQuery).Should().Be("namespace:acme nodeType:Group sort:order");
        ((string?)GlobalSettingsLayoutArea.DataSourcesList().HiddenQuery).Should().Contain(
            $"nodeType:{MeshDataSourceNodeType.NodeType}");
    }

    [Fact]
    public void TheStatusView_DecidesWhatThePanesShow()
    {
        var failed = new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Error,
            CompilationError = "CS0103: The name 'x' does not exist",
            LastCompilationActivityPath = "acme/Widget/_Activity/c1",
        };
        var view = NodeTypeStatusView.From(new MeshNode("Widget", "acme"), failed, TypePath, "en");

        view.Title.Should().Be("Widget", "a nameless type falls back to its id");
        view.StatusBadge.Should().Be("Last compile: Error");
        view.ReleasesStatusBadge.Should().Be("Last compile: Error");
        view.PanelChip.Should().Be("Compilation failed");
        view.CompileLabel.Should().Be("Retry compile");
        view.CompileDisabled.Should().BeFalse();
        view.LogDetail.Should().Contain("CS0103").And.StartWith("```text");
        view.LogLinks.Should().Contain("(/acme/Widget/_Activity/c1)");
        view.ConfigurationCode.Should().Be("config => config");
        view.PendingReleaseNotesStyle.Should().Contain("display: none", "no notes, no line");

        var compiling = NodeTypeStatusView.From(null,
            failed with { CompilationStatus = CompilationStatus.Compiling }, TypePath, "de");
        compiling.CompileDisabled.Should().BeTrue("the button is disabled while a compile runs");
        compiling.PanelChip.Should().Be("Wird kompiliert…", "the words are the viewer's language");

        var noCode = NodeTypeStatusView.From(null, new NodeTypeDefinition(), TypePath, "en");
        noCode.PanelStyle.Should().Contain("display: none", "a type with no code never compiles — no panel");
        noCode.LogStyle.Should().Contain("display: none", "nothing compiled yet — no log");
        noCode.StatusBadge.Should().BeEmpty();

        var upToDate = NodeTypeStatusView.From(new MeshNode("Widget", "acme") { Name = "Widget type" },
            new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = "Widget.dll",
                LatestReleasePath = "acme/Widget/Release/3",
                ReleaseNotes = "Faster",
            }, TypePath, "en");
        upToDate.Title.Should().Be("Widget type");
        upToDate.PanelChip.Should().Be("Up to date");
        upToDate.CompileLabel.Should().Be("Recompile");
        upToDate.ReleasesStatusBadge.Should().BeEmpty("the Releases pane is silent on Ok");
        upToDate.LatestReleaseLink.Should().Be("[3](/acme/Widget/Release/3)");
        upToDate.PendingReleaseNotes.Should().Be("Pending release notes: Faster");
    }

    [Fact]
    public async Task TheRenderedPane_ReceivesTheProjection_AndFollowsALaterEdit()
    {
        var id = "tpl-type-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Configuration = "config => config" },
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(path), new LayoutAreaReference(NodeTypeLayoutAreas.HubConfigViewArea));
        var code = stream.GetDataStream<string>(new JsonPointerReference(
                NodeTypeStatusView.DataContext + "/" + NodeTypeStatusView.Pointer(nameof(NodeTypeStatusView.ConfigurationCode)).Pointer))
            .Replay();
        using var connection = code.Connect();

        await code.Should().Within(TestTimeouts.Convergence).Match(
            c => c == "config => config",
            "the hub publishes the projection where the template's code editor points");

        await Mesh.GetMeshNodeStream(path)
            .Update(n => n with { Content = new NodeTypeDefinition { Configuration = "config => config.AddData(d => d)" } })
            .Should().Within(TestTimeouts.Convergence).Emit();

        await code.Should().Within(TestTimeouts.Convergence).Match(
            c => c == "config => config.AddData(d => d)",
            "the projection stays subscribed, so an edit made while the page is open reaches it — the "
            + "markdown snapshot the page used to bake never could");
    }

    private static IEnumerable<JsonPointerReference> PointersOf(UiControl control)
        => control.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.GetIndexParameters().Length == 0 && p.PropertyType == typeof(object))
            .Select(p => p.GetValue(control))
            .OfType<JsonPointerReference>();
}

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
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;
using static MeshWeaver.Graph.Test.MarkdownEditIsATemplateTest;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#8412 — the NodeType page offered its compile button to a viewer the release request
/// then refused ("You need the Compile permission …"). The node menu already hid Compile and
/// Recycle for that viewer; the page did not ask.
///
/// <para><b>What the arms pin.</b> (1) The projection's viewer fold disables both buttons and states
/// the reason only on a DENIAL — a pending or undetermined check leaves them as the compile state
/// decides (#974). (2) The Create Release button and the compile button bind their disabled state to
/// the projection, and the reason line is on the Overview, Releases and Configuration templates.
/// (3) Rendered for real on a mesh that grants nothing by default, the Overview disables the buttons
/// for a Viewer of the type's partition and leaves them enabled for an Editor of it (control).</para>
///
/// <para>Negative control, run before this file was committed: with <c>ForViewer</c> returning the
/// projection unchanged, arms (1) and (3)'s denied case fail on <c>releaseDisabled == false</c>.</para>
/// </summary>
public class CompileButtonsFollowTheViewersPermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "CompileSpace";
    private const string TypeId = "Widget";
    private const string TypePath = Space + "/" + TypeId;
    private const string Editor = "compile-editor";
    private const string Reader = "compile-reader";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder)
        .AddMeshNodes(
            new MeshNode(Space) { Name = "Compile Space", NodeType = "Space" },
            new MeshNode(TypeId, Space)
            {
                Name = "Widget",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = UpToDate(),
            },
            AssignmentNodeFactory.UserRole(Editor, "Editor", Space),
            AssignmentNodeFactory.UserRole(Reader, "Viewer", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static NodeTypeDefinition UpToDate() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        BuildProvenance = BuildProvenance.Compiled,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Widget.dll",
    };

    [Fact]
    public void OnlyADenial_DisablesTheButtons_AndStatesTheReasonInTheViewersLanguage()
    {
        var view = NodeTypeStatusView.From(new MeshNode(TypeId, Space), UpToDate(), TypePath, "en");
        view.CompileDisabled.Should().BeFalse("an up-to-date type offers Recompile");

        var denied = view.ForViewer(PermissionCheckOutcome.Denied, TypePath, "en");
        denied.CompileDisabled.Should().BeTrue("the release request would refuse this viewer");
        denied.ReleaseDisabled.Should().BeTrue("Create Release is the same request");
        denied.CompileDeniedHint.Should().Contain("Compile permission").And.Contain($"'{Space}'",
            "the reason names the permission and the partition whose roles carry it");
        denied.CompileDeniedHintStyle.Should().NotContain("display: none");

        var german = view.ForViewer(PermissionCheckOutcome.Denied, TypePath, "de");
        german.CompileDeniedHint.Should().Contain("Berechtigung Compile").And.Contain($"'{Space}'",
            "the chrome is the viewer's language");

        foreach (var notADenial in new PermissionCheckOutcome?[]
                 {
                     null, PermissionCheckOutcome.Granted, PermissionCheckOutcome.Undetermined("fold faulted"),
                 })
        {
            var same = view.ForViewer(notADenial, TypePath, "en");
            same.CompileDisabled.Should().BeFalse("only a denial disables (#974: no verdict is not a refusal)");
            same.ReleaseDisabled.Should().BeFalse();
            same.CompileDeniedHint.Should().BeEmpty();
            same.CompileDeniedHintStyle.Should().Contain("display: none");
        }

        var compiling = NodeTypeStatusView.From(null, UpToDate() with { CompilationStatus = CompilationStatus.Compiling },
            TypePath, "en").ForViewer(PermissionCheckOutcome.Granted, TypePath, "en");
        compiling.CompileDisabled.Should().BeTrue("a grant never re-enables a button the compile state disabled");
    }

    [Fact]
    public void TheButtons_BindTheirDisabledState_AndTheReasonLineIsOnEveryPane()
    {
        var members = typeof(NodeTypeStatusView).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);

        NodeTypeLayoutAreas.CreateReleaseButton(TypePath, "en").Disabled.Should()
            .BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("releaseDisabled");
        members.Should().Contain("releaseDisabled").And.Contain("compileDisabled")
            .And.Contain("compileDeniedHint").And.Contain("compileDeniedHintStyle");

        foreach (var (name, template) in new[]
                 {
                     ("Overview compile panel", NodeTypeLayoutAreas.BuildCompileStatusPanel(TypePath)),
                     ("Releases", NodeTypeLayoutAreas.BuildReleasesTemplate(TypePath, "en")),
                     ("Configuration", NodeTypeLayoutAreas.BuildConfigurationTemplate(TypePath, "en")),
                 })
        {
            EveryViewIsStatic(template);
            var controls = Descendants(template).ToList();
            controls.Any(c => c.Style is JsonPointerReference { Pointer: "compileDeniedHintStyle" })
                .Should().BeTrue($"the {name} carries the line that says why its button is disabled");
            controls.OfType<ButtonControl>()
                .Where(b => b.Disabled is JsonPointerReference)
                .Should().NotBeEmpty($"the {name}'s compile/release button binds its disabled state");
        }
    }

    [Fact(Timeout = 60000)]
    public async Task AViewerOfThePartition_IsNotOfferedTheButtons()
    {
        await Field(Reader, nameof(NodeTypeStatusView.ReleaseDisabled)).Should().Within(TestTimeouts.Convergence)
            .Match(d => d == true, "a Viewer holds no Compile, so the request would refuse the click", TestContext.Current.CancellationToken);
        await Field(Reader, nameof(NodeTypeStatusView.CompileDisabled)).Should().Within(TestTimeouts.Convergence)
            .Match(d => d == true, "the Overview's Recompile is the same request", TestContext.Current.CancellationToken);
    }

    /// <summary>Control: the same page for an Editor of the partition, who holds Compile, never
    /// disables the buttons — so the arm above is the permission, not a page that disables for
    /// everyone. Waits for the projection to be published first, so the silence is a verdict.</summary>
    [Fact(Timeout = 60000)]
    public async Task AnEditorOfThePartition_IsOfferedTheButtons()
    {
        await StringField(Editor, nameof(NodeTypeStatusView.PanelChip)).Should().Within(TestTimeouts.Convergence)
            .Match(c => c == "Up to date", "the Overview has published the projection", TestContext.Current.CancellationToken);
        await Field(Editor, nameof(NodeTypeStatusView.ReleaseDisabled)).Where(d => d == true)
            .Should().NotEmit(5.Seconds(), "an Editor holds Compile, so nothing may disable Create Release", TestContext.Current.CancellationToken);
        await Field(Editor, nameof(NodeTypeStatusView.CompileDisabled)).Where(d => d == true)
            .Should().NotEmit(1.Seconds(), "an up-to-date type offers Recompile to whoever may compile it", TestContext.Current.CancellationToken);
    }

    private IObservable<bool?> Field(string viewer, string member)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
        {
            ObjectId = viewer, Name = viewer, Email = $"{viewer}@meshweaver.io",
        });
        return GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(TypePath), new LayoutAreaReference(NodeTypeLayoutAreas.OverviewArea))
            .GetDataStream<bool?>(new JsonPointerReference(
                NodeTypeStatusView.DataContext + "/" + NodeTypeStatusView.Pointer(member).Pointer));
    }

    private IObservable<string?> StringField(string viewer, string member)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
        {
            ObjectId = viewer, Name = viewer, Email = $"{viewer}@meshweaver.io",
        });
        return GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(TypePath), new LayoutAreaReference(NodeTypeLayoutAreas.OverviewArea))
            .GetDataStream<string>(new JsonPointerReference(
                NodeTypeStatusView.DataContext + "/" + NodeTypeStatusView.Pointer(member).Pointer));
    }
}

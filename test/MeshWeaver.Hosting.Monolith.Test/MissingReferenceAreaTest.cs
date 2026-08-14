using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Issue #1456 — <b>an area whose content REFERENCES a node that does not exist</b>.
///
/// <para>Production symptom: <c>fail: Rendering failed for area Present</c> with
/// <c>DeliveryFailureException: No node found at 'ClientDelta/Abschlusspraesentation/04-extraktion'.
/// Closest ancestor is 'ClientDelta/Abschlusspraesentation' (remainder='04-extraktion')</c>. Two
/// things were wrong with that, and neither is "the exception happened":</para>
/// <list type="number">
///   <item>the routing diagnostic — a FRAMEWORK-INTERNAL string — was rendered verbatim into the
///     page, in hard-coded English, inside a code fence;</item>
///   <item>it logged at <c>Error</c>, which auto-files an incident. A deck manifest naming a slide
///     nobody created is bad DATA. Somebody should fix it; nobody should be paged for it.</item>
/// </list>
///
/// <para>So the area now serves a localized frame that names the broken path and nothing else, and
/// — following #1420 — stamps a well-known <see cref="UiControl.Id"/> so the state is
/// mechanically distinguishable from the two that already existed. All three are rendered here and
/// cross-asserted, because the whole value of the marker is that a CONSUMER can tell them apart:</para>
/// <list type="bullet">
///   <item><b>missing reference</b> — the area rendered; the node it points at is absent. TERMINAL.</item>
///   <item><b>compiling</b> — the instance's NodeType is still building. TRANSIENT.</item>
///   <item><b>area not found</b> — no renderer is registered for this area at all. TERMINAL.</item>
/// </list>
/// </summary>
public class MissingReferenceAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The node whose area renders a reference to something that was never created.</summary>
    private const string DeckPath = "decks/Abschlusspraesentation";

    /// <summary>The referenced slide — deliberately NEVER created. This is the broken reference.</summary>
    private const string MissingSlidePath = "decks/Abschlusspraesentation/04-extraktion";

    /// <summary>The instance whose hub carries the compilation-in-progress overlay.</summary>
    private const string OverlaidPath = "decks/Overlaid";

    /// <summary>The NodeType the overlaid instance waits on — held at <c>Compiling</c>.</summary>
    private const string PendingTypePath = "decks/PendingType";

    /// <summary>A plain node hub with no overlay — used for the "genuinely no renderer" direction.</summary>
    private const string PlainPath = "decks/Plain";

    private const string PresentArea = "Present";
    private const string UnregisteredArea = "KeyMetrics";

    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddMeshNodes(MeshNode.FromPath(DeckPath) with
            {
                Name = "Abschlusspraesentation",
                State = MeshNodeState.Active,
                // The production shape of the defect: the area reads a node named by the deck's
                // content. Nothing created that node, so the read faults with the routing NotFound
                // — exactly as the manifest-driven slide read did in the incident. The read is NOT
                // wrapped in a catch: the point is that the FRAMEWORK classifies the failure.
                HubConfiguration = config => config.AddLayout(layout => layout
                    .WithView(PresentArea, (LayoutAreaHost host, RenderingContext _) =>
                        host.Workspace.GetMeshNodeStream(MissingSlidePath)
                            .Select(node => (UiControl?)Controls.Markdown(
                                $"Slide: {node?.Name ?? "(none)"}"))))
            })
            .AddMeshNodes(MeshNode.FromPath(OverlaidPath) with
            {
                Name = "Overlaid",
                State = MeshNodeState.Active,
                HubConfiguration = NodeTypeEnrichmentHelpers
                    .CreateCompilationInProgressConfiguration(PendingTypePath, OverlaidPath)
            })
            .AddMeshNodes(MeshNode.FromPath(PlainPath) with
            {
                Name = "Plain",
                State = MeshNodeState.Active,
                HubConfiguration = config => config
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// The defect itself. Before the fix this frame carried the raw routing diagnostic; the area
    /// now reports the broken reference as its own classified state.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AreaReferencingAMissingNode_RendersAClassifiedMissingReferenceFrame()
    {
        var control = await Render(DeckPath, PresentArea);

        AreaFrameClassifier.IsMissingReference(control).Should().BeTrue(
            "the area rendered — it is the NODE it references that is absent, and that has to be "
            + "mechanically distinguishable from the other two placeholder states");
        AreaFrameClassifier.IsAreaNotFound(control).Should().BeFalse(
            "the Present renderer IS registered — reporting 'area not found' would send an author "
            + "hunting for a missing renderer instead of a missing node");
        AreaFrameClassifier.IsCompileProgress(control).Should().BeFalse();
        AreaFrameClassifier.IsTransientFrame(control).Should().BeFalse(
            "a broken reference is data an author must fix — a waiter must not sit on it forever");
    }

    /// <summary>
    /// What the VIEWER reads. The path is what an author needs; "Closest ancestor" / "remainder="
    /// are routing internals that must never reach a page, and the banner must come from the
    /// localization catalog rather than being hard-coded English.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheFrameNamesTheBrokenPath_ButNeverTheRoutingDiagnostic()
    {
        var control = await Render(DeckPath, PresentArea);

        var markdown = (control as MarkdownControl)?.Markdown?.ToString() ?? string.Empty;
        markdown.Should().Contain(MissingSlidePath,
            "the broken path is the one part of the failure an author can act on");
        markdown.Should().NotContain("Closest ancestor",
            "the routing diagnostic is framework-internal and must never reach an end user");
        markdown.Should().NotContain("remainder=");
        markdown.Should().Contain("Missing reference",
            "the banner comes from the localization catalog (error.missingReference), so a German "
            + "viewer gets German instead of a hard-coded English string");
    }

    /// <summary>
    /// The three-way coupling, rendered end-to-end. Each classifier is asserted against ALL THREE
    /// frames, so a marker that stopped being emitted — or started matching everything — fails here
    /// rather than months later in whichever consumer latched the wrong state.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task MissingReference_Compiling_AndAreaNotFound_AreMechanicallyDistinguishable()
    {
        await CreateCompilingType();

        var missingReference = await Render(DeckPath, PresentArea);
        var compiling = await Render(OverlaidPath, UnregisteredArea);
        var areaNotFound = await Render(PlainPath, UnregisteredArea);

        AreaFrameClassifier.IsMissingReference(missingReference).Should().BeTrue();
        AreaFrameClassifier.IsMissingReference(compiling).Should().BeFalse();
        AreaFrameClassifier.IsMissingReference(areaNotFound).Should().BeFalse();

        AreaFrameClassifier.IsCompileProgress(compiling).Should().BeTrue();
        AreaFrameClassifier.IsCompileProgress(missingReference).Should().BeFalse();
        AreaFrameClassifier.IsCompileProgress(areaNotFound).Should().BeFalse();

        AreaFrameClassifier.IsAreaNotFound(areaNotFound).Should().BeTrue();
        AreaFrameClassifier.IsAreaNotFound(missingReference).Should().BeFalse();
        AreaFrameClassifier.IsAreaNotFound(compiling).Should().BeFalse();

        // Only the compiling one is a promise; the other two are verdicts.
        AreaFrameClassifier.IsTransientFrame(compiling).Should().BeTrue();
        AreaFrameClassifier.IsTransientFrame(missingReference).Should().BeFalse();
        AreaFrameClassifier.IsTransientFrame(areaNotFound).Should().BeFalse();
    }

    private async Task CreateCompilingType()
        => await MeshService.CreateNode(MeshNode.FromPath(PendingTypePath) with
        {
            Name = "PendingType",
            NodeType = "NodeType",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Compiling }
        }).Should().Emit();

    /// <summary>
    /// Renders one area through the layout client — the exact path the GUI drives — and waits for
    /// the first frame that carries a control for it.
    /// </summary>
    private async Task<UiControl?> Render(string addressPath, string area)
    {
        var client = GetClient();
        var reference = new LayoutAreaReference(area);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(addressPath), reference);
        try
        {
            var control = await stream
                .GetControlStream(reference.Area!)
                .Should().Within(60.Seconds())
                .Match(c => c is not null,
                    $"{area} on {addressPath} must produce SOME frame — a hub that produces none is "
                    + "the eternal spinner these placeholders exist to prevent");
            Output.WriteLine($"{addressPath}/{area} → {control?.GetType().Name} (Id={control?.Id})");
            return control;
        }
        finally
        {
            stream.Dispose();
        }
    }
}

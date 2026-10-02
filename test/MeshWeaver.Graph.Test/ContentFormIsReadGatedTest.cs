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
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The property form's fallback slot is READ-GATED in the view.</b>
/// <see cref="OverviewLayoutArea.ContentForm(MeshWeaver.Layout.Composition.LayoutAreaHost, MeshWeaver.Layout.Composition.RenderingContext)"/>
/// is registered as a top-level area (<c>NodeContentForm</c>), so it is addressable by reference on
/// its own — not only as the slot the node page embeds behind that page's own gate. It read the
/// viewer's effective permissions and consumed only <c>Update</c>: without <c>Read</c> it still
/// built the node's property form, while its siblings <c>Overview</c> and <c>Data</c> render the
/// access-denied view.
///
/// <para><b>What is measured.</b> The view's own decision, through the seam that takes the
/// permission stream — the two arms differ in nothing but the permission handed in, on the same
/// node, the same hub and the same viewer. The delivery pipeline's refusal of a
/// <c>SubscribeRequest</c> without Read is a separate, earlier layer and is deliberately not what
/// this test leans on: an area must not depend on the page or the pipeline around it.</para>
///
/// <para><b>Negative control</b>, run before this file was committed: with the read arm removed
/// from <c>ContentForm</c> the denied case fails — the property form arrives where the
/// access-denied view is expected.</para>
/// </summary>
public class ContentFormIsReadGatedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string GatedType = "GatedFormType";
    private const string Space = "GatedForm";
    private const string NodePath = "GatedForm/Record";
    private const string WithoutRead = "FormWithoutRead";
    private const string WithRead = "FormWithRead";

    /// <summary>The content whose property form the slot would render.</summary>
    public record GatedRecord
    {
        /// <summary>A field a viewer without Read must not be shown.</summary>
        public string? Secret { get; init; }
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(GatedType)
            {
                Name = "Gated",
                HubConfiguration = config => config
                    .AddMeshDataSource(s => s.WithContentType<GatedRecord>())
                    .AddLayout(layout => layout
                        .WithView(WithoutRead, (host, _) =>
                            OverviewLayoutArea.ContentForm(host, Observable.Return(Permission.None)))
                        .WithView(WithRead, (host, _) =>
                            OverviewLayoutArea.ContentForm(host, Observable.Return(Permission.Read)))),
            },
            new MeshNode(Space) { NodeType = "Space", Name = Space },
            new MeshNode("Record", Space)
            {
                Name = "Record",
                NodeType = GatedType,
                Content = new GatedRecord { Secret = "TOP" },
            });

    /// <summary>
    /// Without Read the slot renders the access-denied view and never the node-bound form.
    /// </summary>
    [Fact]
    public async Task WithoutRead_TheSlotShowsAccessDenied_AndNoForm()
    {
        var stream = Open(WithoutRead);

        await Walk(stream, WithoutRead).OfType<LabelControl>()
            .Should().Within(TestTimeouts.Convergence)
            .Match(label => label.Data?.ToString() == "Access denied",
                "a viewer without Read on the node gets the access-denied view, like Overview and Data");

        // A negative assertion spends its whole window by construction, and the positive terminal
        // (the denial view) is already in hand, so the window is a fraction of the budget.
        await Walk(stream, WithoutRead).OfType<UiControl>().Where(IsPartOfTheForm)
            .Should().NotEmit(TestTimeouts.Quick / 10,
                "the property form shows the node's content; without Read it must not be rendered at all");
    }

    /// <summary>
    /// The control arm: the same view, node and viewer with Read renders the property form — so
    /// the denial above is the gate's doing, not a form that could not be built.
    /// </summary>
    [Fact]
    public async Task WithRead_TheSlotRendersTheForm()
    {
        var stream = Open(WithRead);

        await Walk(stream, WithRead).OfType<UiControl>()
            .Should().Within(TestTimeouts.Convergence)
            .Match(IsPartOfTheForm,
                "with Read the slot renders the property form over the node's content");
    }

    /// <summary>
    /// A control of the property form: bound to the node itself (the editable form) or to the
    /// area's one-way mirror of the node's content (the read-only form a viewer without Update gets).
    /// </summary>
    private static bool IsPartOfTheForm(UiControl control)
        => control.DataContext is { } context
           && (context == LayoutAreaReference.GetMeshNodeDataContext(NodePath, bindContent: true)
               || context.Contains(EditLayoutArea.GetDataId(NodePath), StringComparison.Ordinal));

    private ISynchronizationStream<JsonElement> Open(string area)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(NodePath), new LayoutAreaReference(area));

    /// <summary>Every control rendered at <paramref name="area"/> and, recursively, in its sub-areas.</summary>
    private static IObservable<UiControl?> Walk(ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Select(control => control is IContainerControl container
                ? container.Areas
                    .Select(named => named.Area?.ToString())
                    .OfType<string>()
                    .Select(subArea => Walk(stream, subArea))
                    .Merge()
                    .StartWith(control)
                : Observable.Return(control))
            .Switch();
}

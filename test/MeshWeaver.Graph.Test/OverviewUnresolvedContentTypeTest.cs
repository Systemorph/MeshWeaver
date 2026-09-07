using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The degrade seam that consulted NO type registry</b> (Systemorph/MeshWeaver#3558).
///
/// <para><c>OverviewLayoutArea.BuildPropertyOverview</c> deserialised a node's
/// <see cref="JsonElement"/> content with a bare <c>JsonSerializer.Deserialize&lt;object&gt;</c>.
/// When the <c>$type</c> discriminator resolves nowhere, <c>ObjectPolymorphicConverter</c> hands
/// back the SAME <see cref="JsonElement"/> — so <c>instance.GetType()</c> became
/// <c>typeof(JsonElement)</c> and the property form was built over <see cref="JsonElement"/>'s own
/// members (<c>ValueKind</c>, …). No exception, no warning, nothing to grep: the page simply looked
/// wrong. That is the exact signature AGENTS.md's payload-cast rule warns about, one layer up.</para>
///
/// <para>The two halves this pins:</para>
/// <list type="number">
/// <item><b>Unresolvable ⇒ say so.</b> A discriminator that resolves on neither route renders an
/// explicit, localized notice — never a form over <see cref="JsonElement"/>.</item>
/// <item><b>The EXACT route is taken.</b> This seam holds the NODE, so it can ask
/// <see cref="IMeshContentTypeRegistry.TryRecoverForNodeType"/> — the question that always has one
/// right answer. The wire converter can only ask the NAME route, which must REFUSE a discriminator
/// two declarations both claim (#1299); the exact route resolves it anyway.</item>
/// </list>
/// </summary>
public class OverviewUnresolvedContentTypeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string UnresolvableView = nameof(UnresolvableView);
    private const string UntypedView = nameof(UntypedView);
    private const string ContestedDiscriminatorView = nameof(ContestedDiscriminatorView);
    private const string ContestedWithoutNodeTypeView = nameof(ContestedWithoutNodeTypeView);

    /// <summary>The NodeType path the contested-name node declares — its own, unique key.</summary>
    private const string ShippingNodeType = "Packages/Shipping/Types/Consignment";

    /// <summary>A SECOND declaration of the same short name, which is what makes it contested.</summary>
    private const string BillingNodeType = "Packages/Billing/Types/Consignment";

    // Instance, never static: the registry's lifetime is this hub's (NoStaticState.md), and a
    // process-wide one would leak the contested claim into every other test in the assembly.
    private readonly MeshContentTypeRegistry registry = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            // The mesh-wide map, reachable from this hub's provider exactly as
            // PersistenceExtensions registers it in production.
            .WithServices(s => s.AddSingleton<IMeshContentTypeRegistry>(registry))
            .AddLayout(layout => layout
                .WithView(UnresolvableView, UnresolvableContentTypeView)
                .WithView(UntypedView, UntypedContentView)
                .WithView(ContestedDiscriminatorView, ContestedDiscriminatorContentView)
                .WithView(ContestedWithoutNodeTypeView, ContestedWithoutNodeTypeContentView));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>A discriminator no declaration in this process claims.</summary>
    private static UiControl UnresolvableContentTypeView(LayoutAreaHost host, RenderingContext ctx)
    {
        var node = new MeshNode("Orphan", "test/unresolved")
        {
            Name = "Orphan",
            NodeType = "Packages/Gone/Types/Vanished",
            Content = Json("""{"$type":"NeverDeclaredAnywhere","title":"hello","count":3}""")
        };
        return OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit: true);
    }

    /// <summary>Free-form JSON content — legal by design, and a DIFFERENT state from an
    /// unresolvable discriminator: there is nothing to wait for.</summary>
    private static UiControl UntypedContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        var node = new MeshNode("FreeForm", "test/untyped")
        {
            Name = "Free form",
            Content = Json("""{"anything":"goes","n":1}""")
        };
        return OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit: true);
    }

    /// <summary>
    /// The name route CANNOT answer here — two declarations claim <c>Consignment</c>, so
    /// <c>TryResolveByDiscriminator</c> refuses on purpose. Only the NodeType route resolves it,
    /// and only a seam that holds the node can take it.
    /// </summary>
    private UiControl ContestedDiscriminatorContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        registry.Register(typeof(Shipping.Consignment), ShippingNodeType);
        registry.Register(typeof(Billing.Consignment), BillingNodeType);

        var node = new MeshNode("Load42", "test/contested")
        {
            Name = "Load 42",
            NodeType = ShippingNodeType,
            Content = Json("""{"$type":"Consignment","Carrier":"DHL","Weight":12}""")
        };
        return OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit: true);
    }

    /// <summary>
    /// The SAME contested content with no NodeType to key on. Neither route can answer — the exact
    /// one has no key, the name one refuses — so this must be the notice. Paired with the view
    /// above it is what makes "the exact route was taken" observable: the two differ ONLY in the
    /// node's NodeType, and they must render differently.
    /// </summary>
    private UiControl ContestedWithoutNodeTypeContentView(LayoutAreaHost host, RenderingContext ctx)
    {
        registry.Register(typeof(Shipping.Consignment), ShippingNodeType);
        registry.Register(typeof(Billing.Consignment), BillingNodeType);

        var node = new MeshNode("Load43", "test/contested-nokey")
        {
            Name = "Load 43",
            Content = Json("""{"$type":"Consignment","Carrier":"DHL","Weight":12}""")
        };
        return OverviewLayoutArea.BuildPropertyOverview(host, node, canEdit: true);
    }

    private async Task<UiControl> RenderAsync(string area)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(CreateHostAddress(), reference);
        return (await stream.GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x != null))!;
    }

    [HubFact]
    public async Task UnresolvableDiscriminator_RendersTheNotice_NotAFormOverJsonElement()
    {
        var control = await RenderAsync(UnresolvableView);

        // Before the fix this was a StackControl carrying a property form built over
        // JsonElement's OWN members — indistinguishable, to a viewer, from the real thing.
        var text = control.Should().BeOfType<MarkdownControl>().Subject.Markdown.ToString()!;
        text.Should().Contain("NeverDeclaredAnywhere",
            "the notice must NAME the discriminator — that string is the whole lead for whoever "
            + "has to decide between 'the compile has not registered it yet' and 'nothing ever will'");
        text.Should().NotContain("ValueKind",
            "ValueKind is a JsonElement member: seeing it means the form was built over the "
            + "degrade type again");
    }

    [HubFact]
    public async Task ContentWithNoDiscriminator_SaysSo_WithoutClaimingATypeIsMissing()
    {
        var control = await RenderAsync(UntypedView);

        var text = control.Should().BeOfType<MarkdownControl>().Subject.Markdown.ToString()!;
        text.Should().NotContain("ValueKind");
        // Free-form JSON is legal and permanent — the copy must not send the reader looking for a
        // compile that will never arrive.
        text.Should().NotContain("NeverDeclaredAnywhere");
    }

    [HubFact]
    public async Task ContestedDiscriminator_ResolvesThroughTheNodeTypeRoute()
    {
        var control = await RenderAsync(ContestedDiscriminatorView);

        // Not the notice: the exact route answered. The name route could not — the discriminator
        // is claimed by two declarations and TryResolveByDiscriminator refuses it by design.
        control.Should().BeOfType<StackControl>(
            "the NodeType route resolves a name the discriminator route must refuse; falling back "
            + "to the notice here would mean this seam never asked it");
    }

    [HubFact]
    public async Task ContestedDiscriminator_WithNoNodeTypeToKeyOn_IsTheNotice()
    {
        var control = await RenderAsync(ContestedWithoutNodeTypeView);

        // Byte-identical content to the test above; the ONLY difference is that this node names no
        // NodeType. The name route refuses a contested discriminator on purpose (answering would
        // deserialise one package's content into the other's record), so there is no answer left.
        var text = control.Should().BeOfType<MarkdownControl>().Subject.Markdown.ToString()!;
        text.Should().Contain("Consignment");
    }

    // Two declarations, one short name — the #1299 shape, reproduced with real CLR types so the
    // registry genuinely marks 'Consignment' ambiguous rather than being told to. Nested in
    // distinct containers so both types are literally named "Consignment".
    /// <summary>Stand-in for a shipping package's declarations.</summary>
    public static class Shipping
    {
        /// <summary>Shipping's consignment record.</summary>
        public record Consignment
        {
            /// <summary>Carrier handling the load.</summary>
            public string Carrier { get; init; } = "";

            /// <summary>Gross weight in kilograms.</summary>
            public int Weight { get; init; }
        }
    }

    /// <summary>Stand-in for an unrelated billing package's declarations.</summary>
    public static class Billing
    {
        /// <summary>Billing's unrelated record of the same name.</summary>
        public record Consignment
        {
            /// <summary>Invoice this consignment was billed on.</summary>
            public string InvoiceId { get; init; } = "";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>WHAT MAY BE CREATED UNDER A NODE IS <see cref="ICreatableTypesProvider"/>'S ANSWER, AND
/// THE CREATE FORM ASKS IT</b> (issue #4040).
///
/// <para><b>The defect.</b> <c>CreateLayoutArea</c> built the type picker from two query literals
/// of its own — <c>namespace: nodeType:NodeType context:create</c> and the
/// <c>scope:selfAndAncestors</c> leg — so a type was offered iff it sat at the root namespace or in
/// the target's own ancestor chain. <see cref="ICreatableTypesProvider.GetCreatableTypes"/> was
/// registered as a singleton and called by NOTHING, which made
/// <see cref="NodeTypeDefinition.CreatableTypes"/>, <see cref="NodeTypeDefinition.IncludeGlobalTypes"/>
/// and <c>MeshConfiguration.GlobalCreatableTypes</c> documented, populated and never read. Measured
/// on memex.systemorph.com on 2026-09-11: <c>Crm/Offer</c> declares
/// <c>"creatableTypes": ["Crm/Question"]</c>, <c>PearlTechnology/Commercials</c> is a
/// <c>Crm/Offer</c>, and its Create area offered ~90 types, none of them <c>Crm/*</c>
/// (<c>Systemorph/MeshWeaver.Crm#82</c> shipped green through every gate on that basis).</para>
///
/// <para><b>The shape reconstructed here</b> is exactly that one: the declaring type and the
/// declared type live in partition <c>Ct…</c>, the INSTANCE lives in partition <c>Ho…</c>, so the
/// declared type is outside the instance's ancestor chain and no namespace-scoped query can reach
/// it. <see cref="TheOldQueryLiteralsCannotReachADeclaredType"/> is the negative control that keeps
/// this honest — it runs the two literals the form used to run and asserts they return nothing of
/// the sort.</para>
///
/// <para>🚨 <b>And the opposite risk, which is the one that would ship silently.</b> Pointing the
/// form at a NARROWER source would shrink every Create menu in the fleet with no error and no empty
/// state — the same invisible failure, aimed the other way.
/// <see cref="AParentDeclaringNothingLosesNothing"/> pins that: for a parent type that declares no
/// <c>CreatableTypes</c>, the offered set must still CONTAIN everything the two literals returned
/// AND every static registration the form used to hand the picker as fixed items. A parent that
/// DOES declare restricts; a parent that declares nothing does not.</para>
/// </summary>
public class CreateMenuHonoursTheParentTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MeshNodePickerControl), typeof(MeshNode));

    /// <summary>
    /// The two query literals <c>CreateLayoutArea</c> ran before this change, verbatim. They are
    /// the BASELINE the new source may never fall below, and the negative control that the
    /// cross-partition declaration was genuinely unreachable through them.
    /// </summary>
    private static string[] RetiredQueryLiterals(string parentPath) =>
    [
        "namespace: nodeType:NodeType context:create",
        $"namespace:{parentPath} nodeType:NodeType scope:selfAndAncestors context:create",
    ];

    /// <summary>
    /// 🚨 THE NO-SHRINK PIN. A parent type declaring no <c>CreatableTypes</c> restricts nothing, so
    /// the provider-fed menu must be a SUPERSET of what the retired literals returned and of the
    /// static items the form used to pass as <c>WithItems</c>.
    ///
    /// <para>Asserted non-vacuously: the baseline itself must be non-empty and must contain the
    /// RUNTIME NodeType sitting in the instance's own partition, so "both sets are empty" cannot
    /// pass. Measured here: baseline 4 + 42 static items, all present in the provider's 45.</para>
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant (TestTimeouts.TestMilliseconds is not).
    public async Task AParentDeclaringNothingLosesNothing()
    {
        var (types, host) = await Fixture();
        var parentPath = $"{host}/Thing";

        var baseline = await LiteralQueryResults(parentPath);
        Output.WriteLine($"retired literals returned {baseline.Count}: {string.Join(", ", baseline.OrderBy(x => x))}");
        baseline.Should().NotBeEmpty("a baseline of nothing cannot detect a shrink");
        baseline.Should().Contain($"{host}/Local",
            "the runtime NodeType in the instance's own partition is what the ancestor-scoped leg is FOR — "
            + "without it in the baseline this comparison is not measuring the query legs at all");

        var staticItems = Mesh.ServiceProvider.EnumerateStaticNodes()
            .Where(n => !n.IsExcludedFromContext(MeshContexts.Create))
            .Select(n => n.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        staticItems.Should().NotBeEmpty("the form passed these as the picker's fixed Items");

        var offered = await Offered(parentPath);
        Output.WriteLine($"provider offered {offered.Count}: {string.Join(", ", offered.OrderBy(x => x))}");

        baseline.Except(offered).OrderBy(x => x).Should().BeEmpty(
            "a parent type that declares no CreatableTypes restricts NOTHING — a type the retired "
            + "query literals offered and the provider does not is a Create menu entry that "
            + "silently disappeared, which is #4040 pointed the other way");
        staticItems.Except(offered).OrderBy(x => x).Should().BeEmpty(
            "the static registrations the form handed the picker as fixed Items are the bulk of the "
            + "menu (Markdown, Group, Role, Redirect, UiContribution, HomeTab, License, WhatsNew — "
            + "none of which carries NodeType=\"NodeType\", which is why filtering on that stamp "
            + "kept 7 of 42)");
        offered.Should().Contain($"{types}/Widget/Part",
            "the second discovery leg — the child NodeTypes the PARENT'S TYPE defines, so a "
            + "{0}/Widget instance offers {0}/Widget/Part. The retired literals could not reach it "
            + "either: it is in the type's partition, not the instance's ancestor chain", types);
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL. The retired literals cannot reach a type declared outside the
    /// instance's ancestor chain — which is precisely why <c>MeshWeaver.Crm#82</c> was inert. If
    /// the literals ever DID reach it, the acceptance test below would be passing for a reason that
    /// has nothing to do with the declaration, and would keep passing after a revert.
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task TheOldQueryLiteralsCannotReachADeclaredType()
    {
        var (types, host) = await Fixture();

        var baseline = await LiteralQueryResults($"{host}/Deal");
        Output.WriteLine($"retired literals for {host}/Deal returned {baseline.Count}: "
            + string.Join(", ", baseline.OrderBy(x => x)));

        baseline.Should().NotBeEmpty("a control that measured nothing controls nothing");
        baseline.Should().NotContain($"{types}/Question",
            "the declared type lives in ANOTHER partition, so no namespace-scoped query from the "
            + "instance reaches it — this is the Crm#82 measurement, reconstructed");
    }

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION, asserted on the RENDERED Create area rather than on the
    /// provider: the type picker a person is actually shown offers <c>{types}/Question</c> because
    /// the parent's NodeType declares it — and the same form rendered on a sibling instance whose
    /// type declares nothing does NOT, so the declaration is demonstrably what put it there.
    ///
    /// <para>The picker also carries NO <see cref="MeshNodePickerControl.Queries"/>: a query leg
    /// would re-admit by discovery exactly what a restricting parent excluded.</para>
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task TheRenderedCreateFormOffersATypeTheParentDeclaresInAnotherPartition()
    {
        var (types, host) = await Fixture();

        var declaring = await RenderedTypePicker($"{host}/Deal");
        var offered = PickerPaths(declaring);
        Output.WriteLine($"rendered picker on {host}/Deal offers {offered.Count}: "
            + string.Join(", ", offered.OrderBy(x => x)));

        declaring.Queries.Should().BeNull(
            "the picker is fed by ICreatableTypesProvider alone; a query leg alongside it would "
            + "re-admit every discovered type and defeat the parent's restriction");
        offered.Should().Contain($"{types}/Question",
            "the parent's NodeType declares CreatableTypes: [\"{0}/Question\"] — that is the whole "
            + "point of the setting, and it is what MeshWeaver.Crm#82 asked for", types);

        var control = PickerPaths(await RenderedTypePicker($"{host}/Thing"));
        control.Should().NotContain($"{types}/Question",
            "the sibling instance's type declares nothing, so nothing ambient offers the type — "
            + "the declaration is what put it in the other menu, not discovery");
    }

    /// <summary>
    /// The RESTRICTING half of the same setting, and the two knobs that ride with it: an explicit
    /// <c>CreatableTypes</c> filters auto-discovery down to the declared set, while
    /// <see cref="NodeTypeDefinition.IncludeGlobalTypes"/> decides whether the global set still
    /// rides along. A type that opted out of <c>context:create</c> is offered by neither.
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task ADeclarationRestrictsDiscovery_AndIncludeGlobalTypesDecidesTheGlobals()
    {
        var (types, host) = await Fixture();
        var globals = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>().GlobalCreatableTypes;
        globals.Should().NotBeEmpty("the global set is the thing IncludeGlobalTypes switches off");

        var declaring = await Offered($"{host}/Deal");
        Output.WriteLine($"declaring parent offers {declaring.Count}: {string.Join(", ", declaring.OrderBy(x => x))}");
        declaring.Should().NotContain($"{host}/Local",
            "a declared CreatableTypes list is a WHITELIST: discovery is filtered down to it, so a "
            + "type the ancestor-scoped query found but the parent did not declare is withheld");
        globals.Except(declaring).OrderBy(x => x).Should().BeEmpty(
            "IncludeGlobalTypes defaults to true, so the global set rides along with the whitelist");

        var sealedOff = await Offered($"{host}/Sealed");
        Output.WriteLine($"IncludeGlobalTypes=false offers {sealedOff.Count}: {string.Join(", ", sealedOff.OrderBy(x => x))}");
        sealedOff.Should().Contain($"{types}/Question", "the declared type is still offered");
        sealedOff.Intersect(globals).OrderBy(x => x).Should().BeEmpty(
            "IncludeGlobalTypes=false is the opt-out, and it is the reason the property carries "
            + "JsonIgnoreCondition.Never — an explicit false must survive the round trip");
    }

    /// <summary>
    /// A NodeType that opted out of the create context (<c>ExcludeFromContext: ["create"]</c> —
    /// Release, Build, ModuleBuild, Partition) is never offered. The retired form filtered these
    /// out of both its Items and its queries; the provider did not, and would have started offering
    /// four uncreatable types the moment it was wired up.
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task ATypeThatOptedOutOfTheCreateContextIsNeverOffered()
    {
        var (_, host) = await Fixture();

        var optedOut = Mesh.ServiceProvider.EnumerateStaticNodes()
            .Where(n => n.IsExcludedFromContext(MeshContexts.Create))
            .Select(n => n.Path)
            .ToArray();
        Output.WriteLine($"opted out of create: {string.Join(", ", optedOut.OrderBy(x => x))}");
        optedOut.Should().NotBeEmpty("a platform with no opt-out cannot show that the opt-out is honoured");

        var offered = await Offered($"{host}/Thing");
        offered.Intersect(optedOut).OrderBy(x => x).Should().BeEmpty(
            "ExcludeFromContext: [\"create\"] is how a type says it is not creatable — the Create "
            + "menu is the one surface that must honour it");
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two partitions, mirroring <c>Crm</c> + <c>PearlTechnology</c>: the TYPES live in one and the
    /// INSTANCES in the other, so a declared type is provably outside the instance's ancestor chain.
    /// </summary>
    private async Task<(string Types, string Host)> Fixture()
    {
        var types = "Ct" + Guid.NewGuid().ToString("N")[..8];
        var host = "Ho" + Guid.NewGuid().ToString("N")[..8];

        await Import(new FakeRepoSource(types)
        {
            Root = Space(types),
            Nodes =
            [
                // Declares nothing — the no-shrink case.
                TypeNode(types, "Widget", new NodeTypeDefinition { Configuration = "config => config" }),
                // A child type of Widget: what an instance of Widget may create by hierarchy.
                TypeNode($"{types}/Widget", "Part", new NodeTypeDefinition { Configuration = "config => config" }),
                // The Crm/Offer shape: declares a type in its own partition, which is NOT in the
                // ancestor chain of the instances that carry this type.
                TypeNode(types, "Offer", new NodeTypeDefinition
                {
                    Configuration = "config => config",
                    CreatableTypes = [$"{types}/Question"],
                }),
                // Same, with the globals switched off.
                TypeNode(types, "SealedOffer", new NodeTypeDefinition
                {
                    Configuration = "config => config",
                    CreatableTypes = [$"{types}/Question"],
                    IncludeGlobalTypes = false,
                }),
                TypeNode(types, "Question", new NodeTypeDefinition { Configuration = "config => config" }),
            ],
        });

        await Import(new FakeRepoSource(host)
        {
            Root = Space(host),
            Nodes =
            [
                // A runtime NodeType in the INSTANCE's partition — what the ancestor-scoped query
                // leg is for, and what makes the baseline non-vacuous.
                TypeNode(host, "Local", new NodeTypeDefinition { Configuration = "config => config" }),
                Instance(host, "Thing", $"{types}/Widget"),
                Instance(host, "Deal", $"{types}/Offer"),
                Instance(host, "Sealed", $"{types}/SealedOffer"),
            ],
        });

        return (types, host);
    }

    private async Task Import(FakeRepoSource source)
    {
        // 🚨 .Await(), never a bare `await source`: Rx's own awaiter resumes the continuation
        // INLINE on the signalling thread, and every later await in the method inherits that
        // scheduler.
        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Output.WriteLine($"{source.Partition} import: {result.Outcome} count={result.Count} "
            + $"failed={result.Failed} blocked=[{string.Join(", ", result.BlockedCreatePaths)}]");
        result.Failed.Should().Be(0, "a fixture that did not land cannot measure anything");
    }

    /// <summary>What <see cref="ICreatableTypesProvider"/> offers at <paramref name="parentPath"/>.</summary>
    private async Task<HashSet<string>> Offered(string parentPath)
    {
        var parent = await Mesh.GetWorkspace().GetMeshNodeStream(parentPath)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        var types = await Mesh.ServiceProvider.GetRequiredService<ICreatableTypesProvider>()
            .GetCreatableTypes(parentPath, parent)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        return types.Select(t => t.NodeTypePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the two retired query literals return — run against the same query core.</summary>
    private async Task<HashSet<string>> LiteralQueryResults(string parentPath)
    {
        var core = Mesh.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in RetiredQueryLiterals(parentPath))
        {
            var change = await core
                .Query<MeshNode>(MeshQueryRequest.FromQuery(query), Mesh.JsonSerializerOptions)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
            foreach (var item in change.Items)
                result.Add(item.Path);
        }
        return result;
    }

    /// <summary>
    /// The type picker of the RENDERED Create area, read through the layout client exactly as the
    /// portal reads it — the assertion is on what a person is offered, not on what a service
    /// returned. Every child area is subscribed at once so a sibling that has not rendered yet
    /// cannot hold the read.
    /// </summary>
    private async Task<MeshNodePickerControl> RenderedTypePicker(string nodePath)
    {
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(nodePath), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Where(c => c is StackControl { Areas.Count: > 0 })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

        var areas = ((StackControl)root!).Areas
            .Select(a => a.Area?.ToString())
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a => stream.GetControlStream(a!))
            .ToArray();

        return await Observable.Merge(areas)
            .OfType<MeshNodePickerControl>()
            .Where(IsTypePicker)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
    }

    private static bool IsTypePicker(MeshNodePickerControl picker) =>
        picker.Data is JsonPointerReference { Pointer: var p }
        && p.TrimStart('/').Equals("type", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> PickerPaths(MeshNodePickerControl picker) =>
        (picker.Items ?? [])
        .OfType<MeshNode>()
        .Select(n => n.Path)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = partition, NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {partition}" }
    };

    private static MeshNode Instance(string partition, string id, string typePath) => new(id, partition)
    {
        NodeType = typePath, Name = id, State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}" }
    };

    private static MeshNode TypeNode(string nameSpace, string id, NodeTypeDefinition definition) =>
        new(id, nameSpace)
        {
            NodeType = MeshNode.NodeTypePath, Name = id, State = MeshNodeState.Active,
            Content = definition
        };

    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}

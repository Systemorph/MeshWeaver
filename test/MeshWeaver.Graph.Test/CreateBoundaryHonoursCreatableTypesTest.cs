using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE PARENT TYPE'S <c>CreatableTypes</c> IS ENFORCED AT THE CREATE BOUNDARY, NOT ONLY IN THE
/// FORM</b> (issue #4077).
///
/// <para><b>The gap.</b> <c>CreateMenuHonoursTheParentTypeTest</c> pins the FORM half: the picker
/// offers only what the parent's NodeType allows, and the form's <c>type</c> VALUE is aligned with
/// that set so submitting without touching the field cannot write a forbidden type. The
/// authoritative boundary — <c>CreateNodeRequest</c> in <c>MeshExtensions</c> — ran the permission
/// pipeline and <c>NodeTypeResolution</c> and asked nothing about the declaration
/// (<c>git grep CreatableTypes src/MeshWeaver.Mesh.Contract/MeshExtensions.cs</c> → 0). So a caller
/// posting a create directly, an agent tool call, or a client forging the layout area's
/// <c>/data/{form}/type</c> value wrote whatever it set: the menu honoured the declaration and the
/// write ignored it.</para>
///
/// <para>🚨 <b>Every test here goes through the WIRE VERB, never the provider.</b> A test that asked
/// <see cref="ICreatableTypesProvider"/> again would be re-measuring the form half — the whole
/// finding is that the two halves disagreed, so only the response to a posted
/// <c>CreateNodeRequest</c> can tell them apart.</para>
///
/// <para>🚨 <b>And the opposite risk, which is the one that would ship silently.</b> A whitelist
/// enforced at the boundary would start REFUSING writes that are legal today — fleet-wide, wearing
/// the look of corruption. <see cref="AParentDeclaringNothingRefusesNothing"/> and
/// <see cref="ThePlatformsOwnWritersAreNotCurated"/> are the two pins that keep that blast radius at
/// zero: nothing is refused unless a type author declared a list, and the platform's own writers
/// (installer, GitSync, migrations — every <c>ImpersonateAsSystem</c> caller, i.e. exactly the
/// writers that fan a create out over hundreds of paths) are never curated.</para>
/// </summary>
public class CreateBoundaryHonoursCreatableTypesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// A person. Not the system identity, because the system identity is the documented bypass —
    /// running the acceptance criterion as system would make it pass for the wrong reason, and keep
    /// passing after a revert.
    /// </summary>
    private static readonly AccessContext Alice = new()
    {
        ObjectId = "alice-creatable-types",
        Name = "Alice",
    };

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION. A create the parent's NodeType does not allow is REFUSED by the
    /// boundary, and the refusal names what went wrong well enough to act on.
    ///
    /// <para>On the failing code this assertion reads <c>Success = true</c>: the node is created,
    /// exactly as it was for anyone who posted the verb instead of using the form.</para>
    ///
    /// <para>Non-vacuous by construction: the SAME create of a type the parent DOES declare, by the
    /// same identity into the same namespace, must succeed — so this cannot pass because the
    /// partition is unwritable, because Alice lacks a grant, or because creates are broken.</para>
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant (TestTimeouts.TestMilliseconds is not).
    public async Task ATypeTheParentDoesNotDeclareIsRefusedAtTheBoundary()
    {
        var (types, host) = await Fixture();

        // The positive control FIRST, so a failure of the fixture cannot be read as enforcement.
        var allowed = await CreateAs(Alice, Child($"{host}/Sealed", "Asked", $"{types}/Question"));
        allowed.Success.Should().BeTrue(
            "{0}/SealedOffer declares CreatableTypes: [\"{1}/Question\"], so this one IS allowed — "
            + "without it succeeding, the refusal below says nothing about the declaration. "
            + "Error was: {2}", types, types, allowed.Error ?? "<none>");

        var refused = await CreateAs(Alice, Child($"{host}/Sealed", "Forbidden", "Markdown"));

        refused.Success.Should().BeFalse(
            "the parent's NodeType declares CreatableTypes and Markdown is not in it — the Create "
            + "form has always withheld this type here, and the write boundary must agree. A "
            + "boundary that accepts what the form withholds makes the declaration advisory");
        refused.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidNodeType,
            "the type is the thing that is wrong — not the caller's entitlements (Unauthorized) "
            + "and not an unestablished check (Unavailable, #1446)");
        refused.Error.Should().Contain("Markdown", "the refusal names the type it refused");
        refused.Error.Should().Contain($"{types}/SealedOffer",
            "it names the PARENT TYPE that declared the restriction — that is where the repair is "
            + "made, and it is not the parent instance");
        refused.Error.Should().Contain($"{types}/Question",
            "it names what IS allowed, so a caller can act on the message alone");
    }

    /// <summary>
    /// <see cref="NodeTypeDefinition.IncludeGlobalTypes"/> decides the globals at the BOUNDARY too,
    /// exactly as it does in the menu. The pair is what makes either half a measurement: the same
    /// type, the same identity, two parents that differ only in that flag.
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task TheGlobalsRideAlongWithAWhitelistUnlessTheParentSwitchedThemOff()
    {
        var (_, host) = await Fixture();
        var globals = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>().GlobalCreatableTypes;
        globals.Should().Contain("Markdown", "the global set is what IncludeGlobalTypes switches off");

        var open = await CreateAs(Alice, Child($"{host}/Deal", "GlobalHere", "Markdown"));
        open.Success.Should().BeTrue(
            "Offer declares CreatableTypes but leaves IncludeGlobalTypes at its default true, so "
            + "the global set rides along — a whitelist NARROWS discovery, it does not seal the "
            + "parent off. Error was: {0}", open.Error ?? "<none>");

        var sealedOff = await CreateAs(Alice, Child($"{host}/Sealed", "GlobalGone", "Markdown"));
        sealedOff.Success.Should().BeFalse(
            "SealedOffer sets IncludeGlobalTypes=false, which is the opt-out — and the boundary "
            + "must read the same flag the menu reads, or the two disagree again one level down");
    }

    /// <summary>
    /// 🚨 THE NO-REFUSAL PIN, and the reason the blast radius of this change is zero for everything
    /// that exists today. "A parent that declares nothing restricts nothing" is the documented
    /// default (<c>Doc/Architecture/CreatableTypes</c>) and it stays literally true at the boundary:
    /// with <c>CreatableTypes</c> absent the validator is a no-op.
    ///
    /// <para>Asserted over two shapes — a global type and a RUNTIME NodeType in the instance's own
    /// partition — because a validator that resolved the parent's definition wrongly (or read a
    /// whitelist where there is none) would start refusing both, fleet-wide, with no error anyone
    /// could trace to a declaration nobody wrote.</para>
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task AParentDeclaringNothingRefusesNothing()
    {
        var (_, host) = await Fixture();

        var global = await CreateAs(Alice, Child($"{host}/Thing", "AnyGlobal", "Markdown"));
        global.Success.Should().BeTrue(
            "Widget declares no CreatableTypes, so nothing under a Widget instance is restricted. "
            + "Error was: {0}", global.Error ?? "<none>");

        var local = await CreateAs(Alice, Child($"{host}/Thing", "AnyLocal", $"{host}/Local"));
        local.Success.Should().BeTrue(
            "and the same for a runtime NodeType — a parent that declares nothing withholds "
            + "nothing, whatever the type's provenance. Error was: {0}", local.Error ?? "<none>");
    }

    /// <summary>
    /// 🚨 THE IMPORT / SYNC BYPASS, pinned. Curation describes what a PERSON may create through the
    /// product; the package installer, GitSync, plugin installs, migrations and repair services are
    /// not curated. Keying the bypass on the SYSTEM identity is what keeps a whitelist from refusing
    /// an import that is legal today — the failure #4077 named as "would look like corruption" —
    /// and it is also why the bulk create path pays no read per node.
    ///
    /// <para>Non-vacuous: <see cref="ATypeTheParentDoesNotDeclareIsRefusedAtTheBoundary"/> refuses
    /// this exact create for a person, so a bypass that stopped working would show up as this test
    /// failing rather than as silence.</para>
    /// </summary>
    [Fact(Timeout = 240000)] // literal: an attribute argument must be a constant.
    public async Task ThePlatformsOwnWritersAreNotCurated()
    {
        var (_, host) = await Fixture();

        var imported = await CreateAsSystem(Child($"{host}/Sealed", "ImportedAnyway", "Markdown"));

        imported.Success.Should().BeTrue(
            "the same create a person is refused must still land for the platform's own writers — "
            + "a declaration that refused an install would turn a curation setting into fleet-wide "
            + "data loss wearing the look of corruption. Error was: {0}", imported.Error ?? "<none>");
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two partitions, the same shape <c>CreateMenuHonoursTheParentTypeTest</c> uses: the TYPES in
    /// one and the INSTANCES in the other, so the declaration is provably what governs and not the
    /// ancestor chain.
    /// </summary>
    private async Task<(string Types, string Host)> Fixture()
    {
        if (fixture is not null)
            return fixture.Value;

        var types = "Cb" + Guid.NewGuid().ToString("N")[..8];
        var host = "Hb" + Guid.NewGuid().ToString("N")[..8];

        await Import(new FakeRepoSource(types)
        {
            Root = Space(types),
            Nodes =
            [
                // Declares nothing — the no-refusal case.
                TypeNode(types, "Widget", new NodeTypeDefinition { Configuration = "config => config" }),
                // Declares a whitelist and leaves the globals on (the default).
                TypeNode(types, "Offer", new NodeTypeDefinition
                {
                    Configuration = "config => config",
                    CreatableTypes = [$"{types}/Question"],
                }),
                // Same whitelist, globals switched off.
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
                TypeNode(host, "Local", new NodeTypeDefinition { Configuration = "config => config" }),
                Instance(host, "Thing", $"{types}/Widget"),
                Instance(host, "Deal", $"{types}/Offer"),
                Instance(host, "Sealed", $"{types}/SealedOffer"),
            ],
        });

        fixture = (types, host);
        return fixture.Value;
    }

    private (string Types, string Host)? fixture;

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

    /// <summary>
    /// Posts the WIRE VERB as <paramref name="identity"/> — the boundary every non-form writer
    /// travels (MCP, agents, scripts, a forged form value), which is the surface this test is about.
    /// </summary>
    private async Task<CreateNodeResponse> CreateAs(AccessContext identity, MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // The identity is established for the POST, which is where the delivery captures it. No
        // Observable.FromAsync / .ToTask bridge: ObserveNodeOperation is the test base's own Task
        // affordance and the impersonation scope is the sanctioned way to post under an identity.
        using var scope = access.SwitchAccessContext(identity);
        return await Posted(node, identity.ObjectId);
    }

    /// <summary>The same post under the SYSTEM identity — the documented import/sync bypass.</summary>
    private async Task<CreateNodeResponse> CreateAsSystem(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var scope = access.ImpersonateAsSystem();
        return await Posted(node, "system");
    }

    private async Task<CreateNodeResponse> Posted(MeshNode node, string who)
    {
        var response = (await ObserveNodeOperation<CreateNodeResponse>(new CreateNodeRequest(node)))
            .Message;
        Output.WriteLine($"create {node.Path} as {who}: success={response.Success} "
            + $"reason={response.RejectionReason} error={response.Error}");
        return response;
    }

    private static MeshNode Child(string parentPath, string id, string typePath) =>
        new(id, parentPath)
        {
            NodeType = typePath, Name = id, State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# {id}" }
        };

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
        public ImmutableList<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}

#pragma warning disable CS1591
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// MeshWeaver#3845 hole 4 as data: an import never moves an ADOPTED NodeType's compile input onto a
/// fingerprint no bundle for this instance's framework identity carries — and every other case
/// imports exactly as it did before.
///
/// <para>Both directions, because either alone is a gate that cannot fail: what is HELD (an adopted
/// type whose sources move with no matching bundle), and what is NOT — a type that is not adopted, a
/// type whose input does not move, a type whose bundle IS on the shelf, a type this import cannot
/// judge, and a mesh with no shelf at all. The design is
/// <c>Doc/Architecture/AdoptThenSyncPerNodeType</c>.</para>
/// </summary>
public class BundleKeyedHoldTest
{
    private const string Space = "Plugins";
    private const string TypePath = $"{Space}/Widget";
    private const string SourcePath = $"{TypePath}/Source/WidgetView";
    private const string Identity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b";

    private static MeshNode Source(string code, string path = SourcePath) =>
        MeshNode.FromPath(path) with
        {
            NodeType = "Code",
            Name = "WidgetView",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Language = "csharp", Code = code },
        };

    private static MeshNode TypeNode(string path = TypePath, IReadOnlyList<string>? sources = null) =>
        MeshNode.FromPath(path) with
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Widget",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Sources = sources },
        };

    /// <summary>The fold the bake and the owner both compute — the value this gate compares.</summary>
    private static string Fingerprint(IEnumerable<MeshNode> sources, string typePath = TypePath)
        => NodeTypeSourceFingerprint.Compute(
            sources, typePath, ImmutableSortedDictionary<string, string>.Empty);

    private static NodeTypeDefinition Adopted(string fingerprint, IReadOnlyList<string>? sources = null) =>
        new()
        {
            Sources = sources,
            BuildProvenance = BuildProvenance.AdoptedVerified,
            AdoptedSourceFingerprint = fingerprint,
            CurrentSourceFingerprint = fingerprint,
        };

    private static PrebuiltBundleInventory Shelf(params (string Path, string Fingerprint)[] entries)
        => new(
            entries
                .GroupBy(e => e.Path, StringComparer.Ordinal)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => g.Select(e => e.Fingerprint).ToImmutableHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            entries.Length,
            SealedReadOutcome.Read);

    private static BundleHoldDecision Decide(
        IReadOnlyList<MeshNode> incoming, IReadOnlyList<MeshNode> current,
        IReadOnlyDictionary<string, NodeTypeDefinition> live, PrebuiltBundleInventory shelf)
        => BundleKeyedHold.Decide(
            Space, incoming, current, live,
            // The TYPED read production uses, never a cast: a definition that arrived as untyped
            // JSON reads as absent through `ContentAs`, and a test that cast would pass for a reason
            // the production path does not share (review on #4595).
            node => node.ContentAs<NodeTypeDefinition>(SerializerOptions), shelf, Identity);

    /// <summary>Serializer options for the typed content read — the fast path for content that is
    /// already a CLR instance, and a real deserialization for anything else.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // ══════════════════════════════════════════════════════════════════════════
    //  HELD — the one case the issue's acceptance criterion is about
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnAdoptedTypeWhoseSourcesMove_WithNoMatchingBundle_IsHeld()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        var decision = Decide(incoming, current, live, Shelf((TypePath, Fingerprint([current[1]]))));

        decision.Holds.Should().BeTrue(
            "the sources would land on a fingerprint no bundle for this identity carries, which is "
            + "exactly the state #3583 measured: the type leaves AdoptedVerified for StaleAdopted");
        var held = decision.Held.Single();
        held.Path.Should().Be(TypePath);
        held.HeldFingerprint.Should().Be(Fingerprint([current[1]]));
        held.WantedFingerprint.Should().Be(Fingerprint([incoming[1]]));
        held.Identity.Should().Be(Identity,
            "a roll makes the judgement void rather than old, so the identity travels with it");
        decision.HeldPaths.Should().Contain("Widget/Source/WidgetView")
            .And.Contain("Widget",
                "the type's own node is held with its sources — its configuration lambda is compile "
                + "input the fingerprint does not cover");
        decision.HoldsNode(Space, SourcePath).Should().BeTrue();
        decision.HoldsNode(Space, $"{Space}/SomethingElse").Should().BeFalse();
    }

    [Fact]
    public void AHeldTypesSHAREDSource_HoldsTheSharerToo()
    {
        // The sharer reads the held type's Source namespace, the shape a `shared=@…` query produces.
        const string SharerPath = $"{Space}/Sharer";
        var shared = new[] { $"namespace:{TypePath}/Source scope:subtree" };
        var current = new[] { TypeNode(), Source("class V { }"), TypeNode(SharerPath, shared) };
        var incoming = new[] { TypeNode(), Source("class V { int n; }"), TypeNode(SharerPath, shared) };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
            [SharerPath] = Adopted(Fingerprint([current[1]], SharerPath), shared),
        };

        // 🚨 The sharer's OWN bundle is on the shelf, so step 5 clears it — the only thing that can
        // hold it is the closure. That is also the case that matters: with the shared source held,
        // the sharer's fold would be neither its current nor its incoming value, so the bundle it
        // does have would not match either.
        var decision = Decide(incoming, current, live,
            Shelf((SharerPath, Fingerprint([incoming[1]], SharerPath))));

        decision.Held.Select(h => h.Path).Should().Equal(SharerPath, TypePath);
        decision.Held.Single(h => h.Path == SharerPath).Reason.Should().Contain("shares held source",
            "holding one side only would move the sharer's fingerprint onto a fold made of the held "
            + "node's OLD text beside the tree's new siblings — which no bundle carries either");
    }

    /// <summary>
    /// 🚨 #7410 — a NodeType NEW in the incoming commit that shares a held source is held with it.
    ///
    /// <para>Measured: a package release added a type that includes a source shared with an existing
    /// adopted type, and changed that source. The existing type was held (no bundle carried its new
    /// fingerprint), so the shared source stayed at its old text — but the new type was never a
    /// candidate (candidates came only from the partition's existing AdoptedVerified types), so it
    /// landed and compiled against the old text: <c>CS0117 '…' does not contain a definition for
    /// '…'</c>, until a roll.</para>
    /// </summary>
    [Fact]
    public void ANewTypeSharingAHeldSource_IsHeldWithIt()
    {
        const string NewTypePath = $"{Space}/Digest";
        const string NewSourcePath = $"{NewTypePath}/Source/DigestView";
        var reads = new[] { $"namespace:{TypePath}/Source scope:subtree", $"namespace:{NewTypePath}/Source scope:subtree" };
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[]
        {
            TypeNode(), Source("class V { public static int RouteOf() => 1; }"),
            TypeNode(NewTypePath, reads), Source("class D { int r = V.RouteOf(); }", NewSourcePath),
        };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        var decision = Decide(incoming, current, live, Shelf((TypePath, Fingerprint([current[1]]))));

        decision.Held.Select(h => h.Path).Should().Equal(new[] { NewTypePath, TypePath },
            "the shared source is held at its OLD text, so a new type that reads it would compile "
            + "against text its own sources were not written for — it must wait with the source");
        var newcomer = decision.Held.Single(h => h.Path == NewTypePath);
        newcomer.HeldBySharing.Should().BeTrue(
            "it is held only because it shares a held source — the root's release re-judges it, so "
            + "it must never be a release trigger of its own");
        newcomer.Reason.Should().Contain("new in this commit");
        decision.HoldsNode(Space, NewTypePath).Should().BeTrue("the new type's own node does not land");
        decision.HoldsNode(Space, NewSourcePath).Should().BeTrue(
            "nor do its own new sources — they land together with the type, on the root's release");
    }

    /// <summary>
    /// 🚨 #7410, the move case (review of #7427, thread 4392214514): a source this commit DELETES, but
    /// the existing type's hold keeps in the partition, still makes a new type that queries its old
    /// namespace a sharer.
    ///
    /// <para>The shared source moves from the adopted type's namespace into the new type's. The adopted
    /// type is held (no bundle carries its new fingerprint), so the deleted source stays — and the new
    /// type, querying both namespaces, would land beside it and compile the old declaration next to
    /// its replacement (a duplicate definition). In the incoming tree its old namespace is empty, so
    /// resolving the new type over that tree alone never reached the retained source.</para>
    /// </summary>
    [Fact]
    public void ANewTypeReadingASourceTheHoldRetainsAfterItsDeletion_IsHeldWithIt()
    {
        const string NewTypePath = $"{Space}/Digest";
        const string MovedSourcePath = $"{NewTypePath}/Source/WidgetView";
        var reads = new[] { $"namespace:{TypePath}/Source scope:subtree", $"namespace:{NewTypePath}/Source scope:subtree" };
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[]
        {
            // The adopted type stays; its source is deleted here and re-added under the new type.
            TypeNode(),
            TypeNode(NewTypePath, reads), Source("class V { }", MovedSourcePath),
        };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        var decision = Decide(incoming, current, live, Shelf((TypePath, Fingerprint([current[1]]))));

        decision.HoldsNode(Space, SourcePath).Should().BeTrue(
            "PRECONDITION: the adopted type is held, so its deleted source is retained in the partition");
        decision.Held.Select(h => h.Path).Should().Contain(NewTypePath,
            "the new type's queries reach the retained source once it lands, beside its replacement — "
            + "it must wait with the hold, not compile both declarations");
        var newcomer = decision.Held.Single(h => h.Path == NewTypePath);
        newcomer.HeldBySharing.Should().BeTrue("it is held only through the retained source");
        newcomer.HeldFingerprint.Should().BeEmpty("a new type has no current fingerprint");
        newcomer.WantedFingerprint.Should().Be(
            Fingerprint([incoming[2]], NewTypePath),
            "the wanted fingerprint is the incoming tree's — the retained source is not part of it");
        decision.HoldsNode(Space, MovedSourcePath).Should().BeTrue(
            "its own new source lands together with it, on the root's release");
    }

    /// <summary>
    /// NEGATIVE CONTROL for #7410: a new type that shares NOTHING held lands as it always did — the
    /// closure reaches new types only through a held source, never by being new.
    /// </summary>
    [Fact]
    public void ANewTypeThatSharesNothingHeld_Lands()
    {
        const string NewTypePath = $"{Space}/Gadget";
        const string NewSourcePath = $"{NewTypePath}/Source/GadgetView";
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[]
        {
            TypeNode(), Source("class V { int n; }"),
            TypeNode(NewTypePath), Source("class G { }", NewSourcePath),
        };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        var decision = Decide(incoming, current, live, Shelf((TypePath, Fingerprint([current[1]]))));

        decision.Held.Select(h => h.Path).Should().Equal(new[] { TypePath },
            "PRECONDITION: the adopted type is held, so the closure runs");
        decision.HoldsNode(Space, NewTypePath).Should().BeFalse("a new type sharing nothing held lands");
        decision.HoldsNode(Space, NewSourcePath).Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  NOT held — five ways, each of which would otherwise freeze a Space
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ATypeThatIsNotAdopted_IsNotHeld()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])) with
            {
                BuildProvenance = BuildProvenance.Compiled,
            },
        };

        Decide(incoming, current, live, Shelf()).Holds.Should().BeFalse(
            "a locally compiled type recompiles from whatever lands — there are no adopted bytes to "
            + "keep its sources in step with");
    }

    [Fact]
    public void AnAlreadyStaleType_IsNotHeld()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])) with
            {
                BuildProvenance = BuildProvenance.StaleAdopted,
            },
        };

        Decide(incoming, current, live, Shelf()).Holds.Should().BeFalse(
            "StaleAdopted is already behind its sources; this gate keeps a type from LEAVING "
            + "AdoptedVerified, it does not bring one back");
    }

    [Fact]
    public void AnUnchangedCompileInput_IsNotHeld()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        // Same code, and a sibling node that is NOT compile input changing beside it.
        var incoming = new[]
        {
            TypeNode(), Source("class V { }"),
            MeshNode.FromPath($"{Space}/Readme") with { NodeType = "Markdown", Name = "Readme" },
        };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        Decide(incoming, current, live, Shelf()).Holds.Should().BeFalse(
            "the type's compile input does not move, so there is nothing to hold — and the rest of "
            + "the Space must not be held either");
    }

    [Fact]
    public void AMatchingBundleOnTheShelf_IsNotHeld()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        Decide(incoming, current, live, Shelf((TypePath, Fingerprint([incoming[1]]))))
            .Holds.Should().BeFalse(
                "the release this import already performs adopts those bytes, so the type goes "
                + "AdoptedVerified → AdoptedVerified — which IS the issue's acceptance criterion");
    }

    [Fact]
    public void AReadingThisImportCannotReproduce_Abstains_RatherThanHolding()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            // The live record's fingerprint is NOT what this Space's tree folds to — the shape of a
            // type whose compile input reaches outside the Space (a shared=@ source in another
            // partition, an include this tree does not carry).
            [TypePath] = Adopted("ffffffffffffffff"),
        };

        var decision = Decide(incoming, current, live, Shelf());

        decision.Holds.Should().BeFalse(
            "a gate that cannot reproduce today's answer must not act on tomorrow's — such a type "
            + "imports as before and may go StaleAdopted, which is honest and announced");
        decision.Abstained.Single().Should().Contain(TypePath)
            .And.Contain("cannot reproduce the live source fingerprint",
                "abstaining is stated, never silent: it is the one branch where the gate declines to "
                + "protect a type");
    }

    [Fact]
    public void ATypeTheRepositoryDropped_IsNotThisGatesBusiness()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[]
        {
            MeshNode.FromPath($"{Space}/Readme") with { NodeType = "Markdown", Name = "Readme" },
        };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        Decide(incoming, current, live, Shelf()).Holds.Should().BeFalse(
            "a type the repository no longer carries is the importer's RETIREMENT decision "
            + "(StaticRepoImportResult.HeldNodeTypePaths), which asks whether instances still exist");
    }

    [Fact]
    public void AnUnreadableShelf_HoldsNothing_BecauseNothingCouldReleaseIt()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };
        var unreadable = new PrebuiltBundleInventory(
            ImmutableDictionary<string, ImmutableHashSet<string>>.Empty, 0, SealedReadOutcome.Unreadable);

        Decide(incoming, current, live, unreadable).Holds.Should().BeFalse(
            "the rule that cannot-tell is never clear-to-proceed applies where a hold can be RELEASED, "
            + "and this one cannot: the release predicate reads the SAME shelf, so one unreadable archive "
            + "would wedge every changed adopted type with nothing able to clear it. Holding needs "
            + "evidence that the bytes are absent, and an unreadable shelf is not that evidence");
    }

    [Fact]
    public void ASharersHold_IsMarkedAsSuch_SoItIsNotItsOwnReleaseTrigger()
    {
        const string SharerPath = $"{Space}/Sharer";
        var shared = new[] { $"namespace:{TypePath}/Source scope:subtree" };
        var current = new[] { TypeNode(), Source("class V { }"), TypeNode(SharerPath, shared) };
        var incoming = new[] { TypeNode(), Source("class V { int n; }"), TypeNode(SharerPath, shared) };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
            [SharerPath] = Adopted(Fingerprint([current[1]], SharerPath), shared),
        };

        var decision = Decide(incoming, current, live,
            Shelf((SharerPath, Fingerprint([incoming[1]], SharerPath))));

        decision.Held.Single(h => h.Path == SharerPath).HeldBySharing.Should().BeTrue(
            "its own wanted fingerprint IS on the shelf, so a release predicate that did not know "
            + "this would re-import, re-hold the same set — the root is still missing — and repeat "
            + "that on every later publication");
        decision.Held.Single(h => h.Path == TypePath).HeldBySharing.Should().BeFalse(
            "the root is held on its own reading and IS a release trigger");
        decision.Held.Should().AllSatisfy(h => h.HeldBySharing.Should().NotBeNull(
            "every entry this code writes is STAMPED — null is reserved for a record written before "
            + "the field existed, and the release reads the three states differently (#4605)"));
    }

    [Fact]
    public void AMeshWithNoShelf_HoldsNothing()
    {
        var current = new[] { TypeNode(), Source("class V { }") };
        var incoming = new[] { TypeNode(), Source("class V { int n; }") };
        var live = new Dictionary<string, NodeTypeDefinition>
        {
            [TypePath] = Adopted(Fingerprint([current[1]])),
        };

        Decide(incoming, current, live, PrebuiltBundleInventory.NotConfigured)
            .Holds.Should().BeFalse(
                "a deployment that consumes no bundles compiles every type here, so a hold would "
                + "wait for a publication that is never coming — hole 1's adjudication one level down");
    }
}

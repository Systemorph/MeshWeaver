using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// RECURSIVE COMPILATION — a NodeType whose sources reach into OTHER types' source folders
/// (<c>shared=@Other/Source</c>). This is not an exotic case: it is what every Store type does, and
/// it is the path that took memex down on 2026-07-27.
///
/// <para><b>The incident.</b> <c>Store/Plugin</c> declares four source specs — its own
/// <c>Source</c> subtree plus <c>shared=@Store/Coupon/Source</c>, <c>shared=@Store/Order/Source</c>
/// and <c>shared=@Store/BillingProfile/Source</c> — and a <c>Test</c> subtree. That expands to NINE
/// separate mesh queries reaching across three other types. Its compile activity log shows
/// <b>90.19s</b> between "Invoking compiler…" and those queries resolving, against <b>2.6s</b> of
/// actual Roslyn work: 97% of the compile was waiting on cross-type source discovery. Every plugin
/// root then blew the 60s settle window and served the "build did not settle" fallback.</para>
///
/// <para>So these tests pin the two halves that make recursive discovery correct and bounded:</para>
/// <list type="bullet">
///   <item><b>Expansion</b> — a shared reference must yield BOTH the folder node itself and its
///     subtree, for every referenced type, with no entry silently dropped. A missing query means a
///     missing source file, which surfaces as a phantom Roslyn error rather than as "discovery
///     lost a query".</item>
///   <item><b>Chunk accumulation</b> — each of those queries streams its Initial in CHUNKS. Taking
///     the first emission yields a PARTIAL source set, and a partial set compiles WRONG. The fold
///     must accumulate across chunks, apply removals, and be idempotent under re-delivery.</item>
/// </list>
/// </summary>
public class RecursiveCompilationTest
{
    /// <summary>The real Store/Plugin declaration — the one that hung. Kept verbatim so this test
    /// tracks the shape that actually ships, not a simplified stand-in.</summary>
    private static readonly IReadOnlyList<string> StorePluginSources =
    [
        "namespace:Source scope:subtree",
        "shared=@Store/Coupon/Source",
        "shared=@Store/Order/Source",
        "shared=@Store/BillingProfile/Source",
    ];

    private const string StorePluginPath = "Store/Plugin";

    // ───────────────────────────────────────────────────────────── expansion (the recursion)

    /// <summary>
    /// A <c>shared=</c> reference to another type's source folder must expand to BOTH forms — the
    /// folder node itself AND its subtree. Only one of the two would silently drop half the shared
    /// code: the folder node alone misses every file inside it, the subtree alone misses code held
    /// on the folder node.
    /// </summary>
    [Fact]
    public void SharedReference_ExpandsToBothTheFolderAndItsSubtree()
    {
        var expanded = CodeQueryResolver
            .Expand("shared=@Store/Coupon/Source", StorePluginPath)
            .ToList();

        expanded.Should().HaveCount(2, "a shared folder contributes both itself and its children");
        expanded.Should().Contain("path:Store/Coupon/Source nodeType:Code");
        expanded.Should().Contain("namespace:Store/Coupon/Source scope:subtree nodeType:Code");
    }

    /// <summary>
    /// The full Store/Plugin declaration must expand to a query for EVERY referenced type — its own
    /// sources plus all three shared folders. This is the count that became nine live mesh queries
    /// in the incident; if a future refactor drops one, the type compiles missing files and the
    /// failure looks like broken source code rather than broken discovery.
    /// </summary>
    [Fact]
    public void RealStorePluginDeclaration_ReachesEveryReferencedType()
    {
        var expanded = CodeQueryResolver
            .ExpandAll(StorePluginSources, CodeQueryResolver.DefaultSources, StorePluginPath)
            .ToList();

        // Own sources, rebased onto the type's own path.
        expanded.Should().Contain(q => q.Contains("Store/Plugin/Source"),
            "the type's own Source subtree must be discovered");

        // Every shared type, in both forms — this is the recursion.
        foreach (var shared in new[] { "Store/Coupon/Source", "Store/Order/Source", "Store/BillingProfile/Source" })
        {
            expanded.Should().Contain($"path:{shared} nodeType:Code",
                $"the shared folder node '{shared}' must be discovered");
            expanded.Should().Contain($"namespace:{shared} scope:subtree nodeType:Code",
                $"the files inside '{shared}' must be discovered");
        }

        // Every entry is a Code query — a malformed expansion would compile the wrong node type.
        expanded.Should().OnlyContain(q => q.Contains("nodeType:Code"));
    }

    /// <summary>
    /// Expansion is PURE and bounded: the same declaration expands identically every time, and a
    /// type that shares a folder inside ITSELF does not recurse into itself. A resolver that
    /// re-entered on self-reference would hang discovery — indistinguishable, from the outside,
    /// from the stall this suite exists for.
    /// </summary>
    [Fact]
    public void Expansion_IsDeterministicAndDoesNotSelfRecurse()
    {
        var first = CodeQueryResolver.ExpandAll(StorePluginSources, CodeQueryResolver.DefaultSources, StorePluginPath).ToList();
        var second = CodeQueryResolver.ExpandAll(StorePluginSources, CodeQueryResolver.DefaultSources, StorePluginPath).ToList();
        first.Should().Equal(second, "expansion must be deterministic — discovery is cached on it");

        // A self-referencing share resolves to a finite query set, it does not expand forever.
        var selfShare = CodeQueryResolver
            .Expand($"shared=@{StorePluginPath}/Source", StorePluginPath)
            .ToList();
        selfShare.Should().HaveCount(2);
    }

    /// <summary>
    /// The files a shared reference is supposed to pull in must actually MATCH the expanded
    /// queries — expansion and matching are used by two different callers (the compiler and the
    /// Sources menu), so a drift between them shows up as "the menu lists it but it didn't
    /// compile".
    /// </summary>
    [Fact]
    public void SharedSourceFiles_MatchTheExpandedQueries()
    {
        var expanded = CodeQueryResolver
            .ExpandAll(StorePluginSources, CodeQueryResolver.DefaultSources, StorePluginPath)
            .ToList();

        CodeQueryResolver.Matches("Store/Coupon/Source/CouponContent", expanded)
            .Should().BeTrue("a file inside a shared folder is part of the compile");
        CodeQueryResolver.Matches("Store/Order/Source/StripeGateway", expanded)
            .Should().BeTrue("every shared type contributes, not just the first");
        CodeQueryResolver.Matches("Store/Plugin/Source/Localizer", expanded)
            .Should().BeTrue("the type's own sources still compile");

        CodeQueryResolver.Matches("Store/Catalog/Source/StoreCatalogLayoutAreas", expanded)
            .Should().BeFalse("a type that was NOT shared must not leak into the compile");
    }

    // ──────────────────────────────────────────────── chunk accumulation (the partial-set hazard)

    private static MeshNode Code(string path)
    {
        var slash = path.LastIndexOf('/');
        return new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = "Code" };
    }

    private static QueryResultChange<MeshNode> Change(QueryChangeType type, params string[] paths) =>
        new() { ChangeType = type, Items = paths.Select(Code).ToList() };

    private static ImmutableDictionary<string, MeshNode> Fold(
        params QueryResultChange<MeshNode>[] changes) =>
        changes.Aggregate(
            ImmutableDictionary<string, MeshNode>.Empty,
            MeshNodeCompilationService.ApplyQueryChange);

    /// <summary>
    /// 🚨 A query's Initial arrives in CHUNKS. Taking the first emission yields a partial source
    /// set, and a partial set compiles WRONG — the missing file surfaces as a phantom Roslyn error
    /// pointing at code that is perfectly fine. The fold must accumulate across every chunk.
    /// </summary>
    [Fact]
    public void ChunkedInitial_AccumulatesInsteadOfTruncating()
    {
        var folded = Fold(
            Change(QueryChangeType.Initial, "Store/Plugin/Source/Localizer"),
            Change(QueryChangeType.Initial, "Store/Coupon/Source/CouponContent"),
            Change(QueryChangeType.Initial, "Store/Order/Source/StripeGateway"));

        folded.Keys.OrderBy(k => k).Should().Equal(
            "Store/Coupon/Source/CouponContent",
            "Store/Order/Source/StripeGateway",
            "Store/Plugin/Source/Localizer");
    }

    /// <summary>Re-delivery is idempotent (keyed by path), so a replayed chunk cannot duplicate a
    /// source file into the compile.</summary>
    [Fact]
    public void RedeliveredChunk_IsIdempotent()
    {
        var folded = Fold(
            Change(QueryChangeType.Initial, "Store/Coupon/Source/CouponContent"),
            Change(QueryChangeType.Initial, "Store/Coupon/Source/CouponContent"),
            Change(QueryChangeType.Updated, "Store/Coupon/Source/CouponContent"));

        folded.Count.Should().Be(1, "the same path must never enter the compile twice");
    }

    /// <summary>A deleted source leaves the set, so a compile started after a deletion does not
    /// resurrect the file.</summary>
    [Fact]
    public void RemovedChange_DropsTheSource()
    {
        var folded = Fold(
            Change(QueryChangeType.Initial, "Store/Order/Source/StripeGateway", "Store/Order/Source/OrderContent"),
            Change(QueryChangeType.Removed, "Store/Order/Source/StripeGateway"));

        folded.Keys.Should().Equal("Store/Order/Source/OrderContent");
    }

    /// <summary>Empty and malformed changes are inert — discovery must not throw on a heartbeat or
    /// an empty chunk, because an exception there fails the whole compile.</summary>
    [Fact]
    public void EmptyOrPathlessChanges_AreInert()
    {
        var seeded = Fold(Change(QueryChangeType.Initial, "Store/Plugin/Source/Localizer"));

        MeshNodeCompilationService.ApplyQueryChange(
                seeded, new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial })
            .Should().BeSameAs(seeded, "an empty chunk changes nothing");

        MeshNodeCompilationService.ApplyQueryChange(seeded, null!)
            .Should().BeSameAs(seeded, "a null change must not throw mid-discovery");
    }
}

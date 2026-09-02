using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The test <see cref="SecurityQueries.AllShapes"/> was written for — *"every query shape this
/// class produces, for the completeness test that pins them"* — and which did not exist: no source
/// file in this repo, or in any satellite, referenced <c>AllShapes</c> at all. A census that nothing
/// reads is a list, not a guard.
///
/// <para>It pins two independent properties of the permission-deciding reads:</para>
/// <list type="number">
///   <item><b>Completeness.</b> Every shape carries <see cref="MeshQueryRequest.CompleteQualifier"/>,
///     and <see cref="SecurityQueries.Enumeration"/> REPLACES a limit rather than honouring it. In
///     this fold a page IS the bug (#2011): a truncated membership read is indistinguishable from
///     "this viewer is in no groups", so a group-derived permission vanishes and a group-scoped
///     DENY fails open — with nothing logged and nothing failing.</item>
///   <item><b>The anchoring census (#2640).</b> Each shape is parsed with the REAL
///     <see cref="QueryParser"/> and classified: does it name a concrete partition as its first
///     path segment, or does it fan out across every partition schema? The set that fans out is
///     declared here, with a reason each, so a NEW unanchored security read cannot be added
///     silently — which is how the population grew to the ~2 s per-page floor measured on
///     memex-cloud. See <c>Doc/Architecture/CrossSchemaFanOutElimination</c> and
///     <c>Doc/Architecture/UnanchoredSecurityReads</c>.</item>
/// </list>
///
/// <para>🚨 The census is a RATCHET, not a to-do list: every entry below is unanchored ON PURPOSE
/// and the reason is the point. Anchoring these to the viewer's partition is the fix #2640's body
/// proposes and it is a SECURITY REGRESSION — a <c>GroupMembership</c> lives under the GROUP node,
/// which may sit in a different partition than the grant that names it, so pinning the read to the
/// viewer truncates it. Nothing goes red in either direction.</para>
/// </summary>
public class SecurityQueryShapesTest
{
    // ————————————————————————— completeness (#2011)

    [Fact]
    public void EveryShapeIsStampedComplete()
    {
        SecurityQueries.AllShapes.Should().NotBeEmpty();
        foreach (var shape in SecurityQueries.AllShapes)
            shape.Should().Contain(MeshQueryRequest.CompleteQualifier,
                $"a permission decided on a PAGE is the defect this class exists to prevent — '{shape}'");
    }

    [Fact]
    public void EnumerationReplacesAnExistingLimit_RatherThanHonouringIt()
    {
        SecurityQueries.Enumeration("nodeType:Role limit:50")
            .Should().Be($"nodeType:Role {MeshQueryRequest.CompleteQualifier}",
                "a fold read must not be truncatable by the string its author wrote");

        SecurityQueries.Enumeration($"nodeType:Role {MeshQueryRequest.CompleteQualifier}")
            .Should().Be($"nodeType:Role {MeshQueryRequest.CompleteQualifier}", "idempotent");

        // The boundary the qualifier regex is written for: a CONTENT filter that merely ends in
        // "limit:" is not the query's limit and must survive untouched.
        SecurityQueries.Enumeration("nodeType:Story content.limit:3")
            .Should().Be($"nodeType:Story content.limit:3 {MeshQueryRequest.CompleteQualifier}");
    }

    /// <summary>
    /// The promise in <see cref="SecurityQueries.AllShapes"/>' own doc — *"a member added without an
    /// entry here is not covered"* — made executable. Every public static STRING-valued property on
    /// the class is a shape the fold issues, so every one of them must appear in the census.
    /// </summary>
    [Fact]
    public void EveryShapeProducingMemberIsInTheCensus()
    {
        var shapes = SecurityQueries.AllShapes.ToHashSet(StringComparer.Ordinal);

        var producers = typeof(SecurityQueries)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

        producers.Should().NotBeEmpty("Roles and Memberships are properties on this class");
        foreach (var producer in producers)
        {
            var value = (string)producer.GetValue(null)!;
            shapes.Should().Contain(value,
                $"{producer.Name} is a shape the fold issues, so AllShapes must carry it — a member "
                + "added without an entry is a permission read nothing pins");
        }
    }

    // ————————————————————————— the anchoring census (#2640)

    /// <summary>
    /// The declared population of security reads that CANNOT be anchored, with the reason each is
    /// global. A shape that is not on this list and does not pin a partition fails the test below.
    /// </summary>
    private static readonly (string Shape, string Reason)[] DeliberatelyGlobal =
    [
        (SecurityQueries.Roles,
            "a custom Role definition may live in any partition; a truncated role set silently "
            + "drops every permission derived from the missing role"),
        (SecurityQueries.Memberships,
            "#2011 — a GroupMembership lives under the GROUP node, which may sit in a different "
            + "partition than the grant that names the group. Pinning this to the viewer's "
            + "partition truncates it: the permission vanishes in one direction and a group-scoped "
            + "DENY stops applying in the other, with nothing logged and nothing failing"),
        (SecurityQueries.GatedNodes("Store/Plugin"),
            "instances of a gated NodeType are authored wherever their owner lives; the gate map "
            + "is matched against a target path from any partition"),
        (SecurityQueries.Scoped("namespace:_Access nodeType:AccessAssignment " + SecurityQueries.ContentProjection),
            "the ROOT scope's anchor is the satellite segment _Access, which resolves to no "
            + "partition — there is no partition name to write"),
        (SecurityQueries.Scoped("namespace: id:_Policy nodeType:PartitionAccessPolicy " + SecurityQueries.ContentProjection),
            "the root scope's policy leg carries the EMPTY namespace by construction, for the same "
            + "reason"),
    ];

    [Fact]
    public void TheUnanchoredPopulationIsExactlyTheDeclaredOne()
    {
        var declared = DeliberatelyGlobal.Select(x => x.Shape).ToHashSet(StringComparer.Ordinal);

        var undeclared = SecurityQueries.AllShapes
            .Where(shape => PinnedPartition(shape) is null && !declared.Contains(shape))
            .ToArray();

        undeclared.Should().BeEmpty(
            "a NEW security read that fans out across every partition schema must be declared in "
            + "DeliberatelyGlobal with the reason it cannot be anchored — that is the whole cost "
            + "model of #2640, and a shape added without one is invisible until it shows up as a "
            + "two-second floor on every page");

        // …and the reverse: a declared entry that has since been ANCHORED must be struck, or the
        // list becomes a fiction that outlives its subject.
        foreach (var (shape, reason) in DeliberatelyGlobal)
            PinnedPartition(shape).Should().BeNull(
                $"'{shape}' is declared global because {reason} — if it now pins a partition, "
                + "remove it from the list rather than leaving a stale justification behind");
    }

    /// <summary>
    /// 🚨 The positive control that makes the census non-vacuous. Every entry above is unanchored,
    /// so a classifier that simply answered "unanchored" to everything would pass the test and
    /// prove nothing. These are the shapes the fold ALSO issues — the per-scope legs, which are the
    /// overwhelming majority at runtime — and they must come back PINNED.
    /// </summary>
    [Theory]
    [InlineData("namespace:rbuergi/_Access nodeType:AccessAssignment select:path,id limit:all", "rbuergi")]
    [InlineData("namespace:acme/Renewals/_Access nodeType:AccessAssignment limit:all", "acme")]
    [InlineData("path:Admin/_Access scope:children nodeType:AccessAssignment limit:all", "Admin")]
    [InlineData("namespace:Doc id:_Policy nodeType:PartitionAccessPolicy limit:all", "Doc")]
    public void AnAnchoredScopeLegPinsItsPartition(string query, string expected)
        => PinnedPartition(query).Should().Be(expected);

    [Theory]
    [InlineData("nodeType:Role scope:subtree limit:all")]
    [InlineData("namespace: id:_Policy limit:all")]
    [InlineData("namespace:_Access nodeType:AccessAssignment limit:all")]
    [InlineData("path:*/Source scope:subtree nodeType:Code")]
    public void AnUnanchoredShapePinsNothing(string query)
        => PinnedPartition(query).Should().BeNull();

    /// <summary>
    /// The partition a query resolves to, or <c>null</c> when it fans out — the rule the Postgres
    /// router applies (<c>PostgreSqlPartitionedMeshQuery</c> routes purely by the first path
    /// segment), reproduced here on the shared <see cref="QueryParser"/> so the census is measured
    /// rather than asserted.
    ///
    /// <para>A leading <c>_</c> segment is NOT a partition: <c>_Access</c>, <c>_Thread</c> and
    /// friends are satellite names, and the fold's root leg (<c>namespace:_Access</c>) is exactly
    /// the case <c>SecurityQueries</c> documents as resolving to no partition and falling through
    /// to the cross-schema fan-out. A <c>*</c> first segment is a wildcard over partitions, which
    /// is a fan-out by definition.</para>
    /// </summary>
    private static string? PinnedPartition(string query)
    {
        var parsed = new QueryParser().Parse(query);
        var fromPath = PartitionSegment(parsed.Path);
        if (fromPath is not null)
            return fromPath;
        foreach (var ns in parsed.ExtractNamespaces())
        {
            var fromNamespace = PartitionSegment(ns);
            if (fromNamespace is not null)
                return fromNamespace;
        }
        return null;
    }

    private static string? PartitionSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.Trim().Trim('/');
        if (trimmed.Length == 0)
            return null;
        var slash = trimmed.IndexOf('/');
        var first = slash > 0 ? trimmed[..slash] : trimmed;
        return first.Length == 0 || first == "*" || first.StartsWith('_') || first.Contains('*')
            ? null
            : first;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

    // ————————————————————————— the anchoring census (#2640, #2194)

    /// <summary>
    /// The declared population of security reads that CANNOT be anchored, with the reason each is
    /// global. A shape that is not on this list and fans out fails the test below.
    ///
    /// <para>🚨 #2194 struck the two ROOT legs from this list — not because they were anchored to
    /// the viewer (the truncation this class forbids), but because the router never treated them as
    /// the census assumed. <c>namespace:_Access</c> has a first segment, and a <c>_</c>-prefixed
    /// first segment resolves through the REGISTERED global-satellite definitions (<c>_Access</c> →
    /// <c>system_access</c>): ONE schema. The root <c>_Policy</c> leg was spelled <c>namespace:
    /// id:_Policy</c> — no first segment at all — and so it DID fan out, 179 times in five minutes on
    /// memex-cloud (2026-09-02), for a row that cannot exist on Postgres: an unregistered <c>_</c>
    /// first segment is unroutable, so no write can land a root <c>_Policy</c> there. It is now
    /// spelled <c>path:_Policy</c>, the same node with a first segment.</para>
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
    ];

    [Fact]
    public void TheUnanchoredPopulationIsExactlyTheDeclaredOne()
    {
        var declared = DeliberatelyGlobal.Select(x => x.Shape).ToHashSet(StringComparer.Ordinal);

        var undeclared = SecurityQueries.AllShapes
            .Where(shape => RouteOf(shape).Kind == Route.FanOut && !declared.Contains(shape))
            .ToArray();

        undeclared.Should().BeEmpty(
            "a NEW security read that fans out across every partition schema must be declared in "
            + "DeliberatelyGlobal with the reason it cannot be anchored — that is the whole cost "
            + "model of #2640, and a shape added without one is invisible until it shows up as a "
            + "two-second floor on every page");

        // …and the reverse: a declared entry that has since stopped fanning out must be struck, or
        // the list becomes a fiction that outlives its subject.
        foreach (var (shape, reason) in DeliberatelyGlobal)
            RouteOf(shape).Kind.Should().Be(Route.FanOut,
                $"'{shape}' is declared global because {reason} — if it no longer fans out, "
                + "remove it from the list rather than leaving a stale justification behind");
    }

    /// <summary>
    /// The root grants leg (#2194). <c>_Access</c> is a REGISTERED global satellite, so the router
    /// serves this read from its one registered schema — it was never a fan-out, and the census
    /// that declared it one was wrong about the router, not about the data. The registry this
    /// mirrors is asserted against the real mesh by <see cref="SecurityQueryRootLegRegistryTest"/>.
    /// </summary>
    [Fact]
    public void TheRootAccessLegPinsTheRegisteredGlobalSchema()
    {
        var route = RouteOf(SecurityQueries.RootAssignments);
        route.Kind.Should().Be(Route.Pinned,
            "a root-scope AccessAssignment lives at _Access/{id}, and _Access resolves through the "
            + "registered global-satellite definitions to ONE schema");
        route.Target.Should().Be("system_access");
    }

    /// <summary>
    /// The root policy leg (#2194). Spelled by PATH it has a first segment, so the router never
    /// UNIONs every partition schema for it; the path-less spelling it replaced is kept here as the
    /// control that DID fan out. Today the segment is unregistered, so the read is unroutable (and
    /// answered empty — exactly what the fan-out returned, 179 times per five minutes); registering
    /// <c>_Policy</c> as a global satellite flips this to <see cref="Route.Pinned"/> with no change
    /// to the query, at which point this assertion is the one to update.
    /// </summary>
    [Fact]
    public void TheRootPolicyLegNeverFansOut()
    {
        RouteOf(SecurityQueries.RootPolicy).Kind.Should().Be(Route.Unroutable,
            "path:_Policy names the same node as `namespace: id:_Policy` but gives the router a first "
            + "segment; no global satellite is registered for _Policy, so it routes to no schema "
            + "rather than to all of them");

        // The control: the spelling #2194 replaced fanned out, and would again.
        RouteOf("namespace: id:_Policy nodeType:PartitionAccessPolicy limit:all").Kind
            .Should().Be(Route.FanOut, "the path-less spelling has no first segment to route on");
    }

    /// <summary>
    /// 🚨 The guard for the shapes MEASURED fanning out on memex-cloud (2026-09-02, five-minute
    /// Loki window, <c>[CrossSchema] SLOW</c>, image ci.7616 — the shape is what
    /// <c>PostgreSqlCrossSchemaQueryProvider.DescribeQueryShape</c> logs). The fold must not be the
    /// source of any of these again. <c>PartitionAccessPolicy path:- scope:Children</c> WAS the
    /// fold's root policy leg (179 lines); the three <c>AccessAssignment path:-</c> shapes never
    /// were — <c>scope:Subtree</c> (313 lines) is unattributed in this repo and
    /// <c>scope:Exact</c> is <c>UserActivityLayoutAreas.ObserveSharedTargets</c>, a home-page
    /// band, not a permission read — but a fold shape that DESCRIBED to one would be exactly the
    /// regression this census exists to catch, so all four are pinned.
    /// </summary>
    [Theory]
    [InlineData("nodeType:PartitionAccessPolicy path:- scope:Children")]
    [InlineData("nodeType:AccessAssignment path:- scope:Children")]
    [InlineData("nodeType:AccessAssignment path:- scope:Exact")]
    [InlineData("nodeType:AccessAssignment path:- scope:Subtree")]
    public void TheFoldNeverIssuesAMeasuredFanOutShape(string measured)
        => SecurityQueries.AllShapes.Select(Describe).Should().NotContain(measured,
            "this shape was measured fanning out across every partition schema on memex-cloud; a "
            + "fold read must carry a first segment (path:/namespace:) — see "
            + "Doc/Architecture/CrossSchemaFanOutElimination");

    /// <summary>
    /// 🚨 <b>NO shape the fold issues is path-LESS any more.</b> Fan-out is opt-in: a storage
    /// provider refuses a query that names no partition and did not ask to span them, so a fold read
    /// is now either anchored to a partition or carries <see cref="SecurityQueries.ExplicitFanOut"/>
    /// and has said "every partition" out loud. A shape that logs as <c>path:-</c> would be REFUSED
    /// at runtime, which for the fold means a permission that cannot be evaluated.
    ///
    /// <para>This replaces the older, weaker form of this census ("every path-less shape is one of
    /// the declared globals"). That version became VACUOUS the moment the globals started declaring
    /// themselves: its loop body only ran for <c>path:-</c> shapes, so with none left it asserted
    /// nothing at all while still passing green. The invariant below has the opposite shape — it
    /// fails when a path-less one appears — so it cannot pass by finding nothing.</para>
    /// </summary>
    [Fact]
    public void NoShapeTheFoldIssuesIsPathLess()
    {
        SecurityQueries.AllShapes.Should().NotBeEmpty("a census over nothing proves nothing");

        var pathLess = SecurityQueries.AllShapes
            .Where(shape => Describe(shape).Contains(" path:- ", StringComparison.Ordinal))
            .ToArray();

        pathLess.Should().BeEmpty(
            "a query with no first segment is refused by the storage provider as insufficiently "
            + "specified — anchor it, or declare the fan-out with SecurityQueries.ExplicitFanOut. "
            + "See Doc/Architecture/CrossSchemaFanOutElimination");
    }

    /// <summary>
    /// The other half: the deliberately mesh-wide reads still ARE mesh-wide. Making them explicit
    /// must not have quietly anchored them — anchoring the fold is truncation, and a truncated
    /// membership set makes a group-scoped deny fail open (#2011). So each declared global must
    /// still route as a fan-out, and must carry the marker that makes that legal.
    /// </summary>
    [Fact]
    public void EveryDeliberateGlobalDeclaresItsFanOutAndStillFansOut()
    {
        DeliberatelyGlobal.Should().NotBeEmpty();
        foreach (var (shape, reason) in DeliberatelyGlobal)
        {
            shape.Should().Contain(SecurityQueries.ExplicitFanOut,
                $"a mesh-wide fold read must declare it — {reason}");
            RouteOf(shape).Kind.Should().Be(Route.FanOut,
                $"declaring the fan-out must not have anchored it — {reason}");
        }
    }

    /// <summary>
    /// 🚨 The positive control that makes the census non-vacuous. Every declared entry is a fan-out,
    /// so a classifier that simply answered "fan-out" to everything would pass the test and prove
    /// nothing. These are the shapes the fold ALSO issues — the per-partition legs, which are the
    /// overwhelming majority at runtime — and they must come back PINNED to the router's schema
    /// (the lowercased first segment, exactly as <c>PostgreSqlPartitionedMeshQuery</c> spells it).
    /// </summary>
    [Theory]
    [InlineData("path:rbuergi scope:descendants nodeType:AccessAssignment select:path,id limit:all", "rbuergi")]
    [InlineData("path:acme scope:descendants nodeType:AccessAssignment limit:all", "acme")]
    [InlineData("path:Admin scope:descendants nodeType:AccessAssignment limit:all", "admin")]
    [InlineData("path:Doc scope:descendants id:_Policy nodeType:PartitionAccessPolicy limit:all", "doc")]
    public void AnAnchoredScopeLegPinsItsPartition(string query, string expected)
    {
        var route = RouteOf(query);
        route.Kind.Should().Be(Route.Pinned);
        route.Target.Should().Be(expected);
    }

    /// <summary>
    /// The two shapes the fold actually issues per partition (#3093) come back PINNED — the same
    /// positive control as above, but taken from the BUILDERS rather than from strings written
    /// here, so a change to <see cref="SecurityQueries.PartitionAssignments"/> that quietly stopped
    /// anchoring cannot pass while a hand-copied literal keeps saying it does.
    ///
    /// <para>🚨 Anchoring THESE is not the truncation the census forbids. The forbidden move is
    /// pinning a GLOBAL read (memberships, roles, gated types) to the viewer's partition, which
    /// drops records that live elsewhere. A grant on any scope of a path lives at
    /// <c>{scope}/_Access</c> where the scope is a PREFIX of that path — so it is in that path's
    /// own partition by construction, and the partition read is a superset of the per-scope walk it
    /// replaced.</para>
    /// </summary>
    [Fact]
    public void ThePerPartitionLegsPinTheirPartition()
    {
        RouteOf(SecurityQueries.PartitionAssignments("acme")).Should().Be((Route.Pinned, "acme"));
        RouteOf(SecurityQueries.PartitionPolicies("acme")).Should().Be((Route.Pinned, "acme"));
        // "Admin" is PermissionEvaluator.AdminScope, which is internal to Mesh.Contract.
        RouteOf(SecurityQueries.PartitionAssignments("Admin")).Should().Be((Route.Pinned, "admin"),
                "the Admin partition is excluded from searchable_schemas, so its grants are only "
                + "reachable through a path-anchored read — that is why the fold used to carry an "
                + "Admin special case, and why every partition now takes that route");
    }

    [Theory]
    [InlineData("nodeType:Role scope:subtree limit:all")]
    [InlineData("namespace: id:_Policy limit:all")]
    [InlineData("path:*/Source scope:subtree nodeType:Code")]
    public void AnUnanchoredShapeFansOut(string query)
        => RouteOf(query).Kind.Should().Be(Route.FanOut);

    // ————————————————————————— the classifier

    /// <summary>How the Postgres router answers a query — the three outcomes it actually has.</summary>
    private enum Route
    {
        /// <summary>One concrete schema: the lowercased first segment, or a registered global satellite's schema.</summary>
        Pinned,
        /// <summary>A first segment the router refuses to route (an unregistered <c>_</c>-prefixed segment): no schema, no fan-out, no write can ever land there.</summary>
        Unroutable,
        /// <summary>No first segment, or a wildcard one: a <c>UNION ALL</c> over every partition schema.</summary>
        FanOut,
    }

    /// <summary>
    /// The global satellite namespaces the platform registers with an explicit schema
    /// (<c>DefaultPartitionProvider.CreateGlobalSatellitePartition</c>) — the registry the router's
    /// <c>ResolveGlobalSchema</c> consults for a <c>_</c>-prefixed first segment. Mirrored here so
    /// the shapes test stays a pure parser test; <see cref="SecurityQueryRootLegRegistryTest"/>
    /// asserts this mirror against the REAL registry of a running mesh, so it cannot drift silently.
    /// </summary>
    internal static readonly ImmutableDictionary<string, string> RegisteredGlobalSatellites =
        ImmutableDictionary<string, string>.Empty.Add(SecurityQueries.RootAccessNamespace, "system_access");

    /// <summary>
    /// The route a query takes, reproduced on the shared <see cref="QueryParser"/> from the rules
    /// <c>PostgreSqlPartitionedMeshQuery</c> / <c>PostgreSqlPathRoutingAdapter</c> apply — first
    /// segment of <c>path:</c> (which a single <c>namespace:</c> also sets), lowercased, unless it
    /// is a wildcard (fan-out), empty (fan-out), or <c>_</c>-prefixed (registered → its schema,
    /// otherwise unroutable) — so the census is measured rather than asserted.
    /// </summary>
    private static (Route Kind, string? Target) RouteOf(string query)
    {
        var parsed = new QueryParser().Parse(query);
        var fromPath = RouteOfSegment(parsed.Path);
        if (fromPath.Kind != Route.FanOut)
            return fromPath;
        foreach (var ns in parsed.ExtractNamespaces())
        {
            var fromNamespace = RouteOfSegment(ns);
            if (fromNamespace.Kind != Route.FanOut)
                return fromNamespace;
        }
        return (Route.FanOut, null);
    }

    private static (Route Kind, string? Target) RouteOfSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (Route.FanOut, null);
        var trimmed = path.Trim().Trim('/');
        if (trimmed.Length == 0)
            return (Route.FanOut, null);
        var slash = trimmed.IndexOf('/');
        var first = slash > 0 ? trimmed[..slash] : trimmed;
        if (first.Length == 0 || first == "*" || first.Contains('*'))
            return (Route.FanOut, null);
        if (first.StartsWith('_'))
            return RegisteredGlobalSatellites.TryGetValue(first, out var schema)
                ? (Route.Pinned, schema)
                : (Route.Unroutable, null);
        return (Route.Pinned, first.ToLowerInvariant());
    }

    /// <summary>
    /// The shape <c>PostgreSqlCrossSchemaQueryProvider.DescribeQueryShape</c> puts on the
    /// <c>[CrossSchema] SLOW</c> line — <c>nodeType:{type|*} path:{path|-} scope:{Scope}</c> — so
    /// the census here and the Loki census speak the same vocabulary.
    /// </summary>
    private static string Describe(string query)
    {
        var parsed = new QueryParser().Parse(query);
        return $"nodeType:{parsed.ExtractNodeType() ?? "*"}"
            + $" path:{(string.IsNullOrEmpty(parsed.Path) ? "-" : parsed.Path)}"
            + $" scope:{parsed.Scope}";
    }
}

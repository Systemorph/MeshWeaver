using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The ONE place the permission-deciding mesh queries are written — every read the security fold
/// (<c>PermissionEvaluator</c>) makes to decide what a viewer may see.
///
/// <para>🚨 <b>In the security fold, "no result" and "not allowed" must never be the same value.</b>
/// Several of these reads are GLOBAL by necessity — a <c>GroupMembership</c> lives under the group
/// node, which may sit in a different partition than the grant that names the group, so the query
/// carries no <c>path:</c> and no <c>namespace:</c>. On Postgres that is the shape
/// <c>PostgreSqlCrossSchemaQueryProvider</c> serves by UNION-ing every partition schema, ordering
/// by <c>last_modified DESC</c>. If such a read comes back CLIPPED, a caller cannot tell a page
/// from the whole set (<see cref="MeshQueryRequest.Complete"/>) — and here the difference is a
/// PERMISSION: a truncated membership list is indistinguishable from "this viewer is in no groups",
/// so a group-derived permission simply vanishes and every surface gated on it disappears at once —
/// with nothing logged and nothing failing (issue #2011).</para>
///
/// <para>🚨 The clip this guards against is no longer a DEFAULT the fan-out applies. A second,
/// paging fan-out shape did substitute 50 rows for an unlimited query, and it is deleted (#2048)
/// because no runtime caller ever reached it; the one shape the runtime executes states no limit
/// unless the caller does. What survives — and is why this class is not ceremony — is that a
/// <c>limit:N</c> arriving IN THE QUERY STRING is honoured, and any future bound would be applied
/// here. <see cref="Enumeration"/> overwrites such a limit rather than honouring it, so a fold read
/// cannot be truncated by the string it was written with, whatever the storage layer later
/// decides.</para>
///
/// <para>It is worse than a lost grant in one direction that matters. A group-scoped <b>deny</b>
/// (<c>AccessAssignment</c> with <c>Denied = true</c> whose subject is a group) is applied only to
/// the viewers the membership read says are in that group — so the same truncation makes a
/// revocation FAIL OPEN, leaving a viewer reading content the deny was written to take away.</para>
///
/// <para>The trigger is GROWTH, not a change: it fires the moment a mesh's <c>Role</c> or
/// <c>GroupMembership</c> set outgrows a page, so it appears on the largest install first — the one
/// where it is most expensive — and nobody will have touched anything.</para>
///
/// <para><b>Why a builder rather than a review rule.</b> Every query the fold issues goes through
/// <see cref="Enumeration"/>, which stamps <see cref="MeshQueryRequest.CompleteQualifier"/> on it.
/// A query string that never reaches this class is the only way back to the defect, and a query
/// string that DOES reach it cannot come out truncatable — including one that arrives already
/// carrying a limit, which is overwritten rather than honoured, because in this fold a page IS the
/// bug.</para>
/// </summary>
public static class SecurityQueries
{
    /// <summary>The projection for reads whose CONTENT is folded (roles, memberships, grants, policies).</summary>
    public const string ContentProjection = "select:path,id,namespace,name,nodeType,content";

    /// <summary>The projection for reads that only need to know a node EXISTS at a path (gated nodes).</summary>
    public const string IdentityProjection = "select:path,id,namespace,name,nodeType";

    /// <summary>Node type of a custom role definition.</summary>
    public const string RoleNodeType = "Role";

    /// <summary>Node type of a group-membership record.</summary>
    public const string GroupMembershipNodeType = "GroupMembership";

    // A standalone `limit:<value>` qualifier — at the start of the query or after whitespace, so a
    // `content.limit:3` filter or a free-text word ending in "limit:" is left alone.
    private static readonly Regex LimitQualifier =
        new(@"(?<=^|\s)limit:\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Stamps <paramref name="query"/> as an ENUMERATION — the read whose result the security fold
    /// treats as the COMPLETE set. Idempotent, and total: a query that already states a limit has it
    /// REPLACED, because a permission decision taken on a page is the defect this exists to prevent.
    /// </summary>
    /// <param name="query">The mesh query string.</param>
    /// <returns>The same query, guaranteed to carry <see cref="MeshQueryRequest.CompleteQualifier"/>.</returns>
    public static string Enumeration(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var trimmed = query.Trim();
        return LimitQualifier.IsMatch(trimmed)
            ? LimitQualifier.Replace(trimmed, MeshQueryRequest.CompleteQualifier, 1)
            : $"{trimmed} {MeshQueryRequest.CompleteQualifier}";
    }

    /// <summary>
    /// A GLOBAL security read over every instance of <paramref name="nodeType"/> in the mesh.
    /// Mesh-wide on purpose — the subject and the grant that names it may live in different
    /// partitions — and therefore always an enumeration.
    ///
    /// <para>🚨 <b><c>partitions:all</c> is what makes the fan-out DECLARED, and it is load-bearing.</b>
    /// Fan-out is opt-in: a storage provider refuses a query that names no partition and did not ask
    /// to span them, because a silent cross-schema UNION locks every relation it touches and stalls
    /// unrelated pinned reads (measured memex-cloud 2026-09-02: 7 786 fan-outs in 30 min, 4 328 over
    /// 199 of 199 schemas). These reads are the legitimate exception — so they SAY SO here rather
    /// than being the accident that the refusal exists to catch.</para>
    ///
    /// <para>🚨 <b>Do not "fix" this by anchoring it.</b> Narrowing the fold to a viewer's partition
    /// is TRUNCATION, not optimisation: a <c>GroupMembership</c> lives under its GROUP while the
    /// grant naming it lives elsewhere, so a partition-scoped read silently drops memberships — and
    /// a group-scoped DENY that loses its membership rows fails OPEN (#2011), with nothing logged.
    /// The cost is real and the lever is materialisation, never a narrower query. See
    /// <c>Doc/Architecture/CrossSchemaFanOutElimination</c> and <c>Doc/Architecture/UnanchoredSecurityReads</c>.</para>
    /// </summary>
    /// <param name="nodeType">The node type to enumerate.</param>
    /// <param name="projection">The <c>select:</c> projection; content by default.</param>
    /// <returns>The query string.</returns>
    public static string Global(string nodeType, string projection = ContentProjection)
        => Enumeration($"nodeType:{nodeType} {ExplicitFanOut} scope:subtree {projection}");

    /// <summary>
    /// The qualifier that turns an unanchored read into an EXPLICITLY cross-partition one. A query
    /// carrying it has said "every partition" out loud; a query without any anchor at all has said
    /// nothing, and is refused rather than silently served the most expensive plan the mesh has.
    /// </summary>
    public const string ExplicitFanOut = ParsedQuery.CrossPartitionQualifier;

    /// <summary>Every custom <c>Role</c> definition in the mesh (<c>$security-roles</c>).</summary>
    public static string Roles => Global(RoleNodeType);

    /// <summary>Every <c>GroupMembership</c> record in the mesh (<c>$security-memberships</c>).</summary>
    public static string Memberships => Global(GroupMembershipNodeType);

    /// <summary>
    /// Every instance of a type-declared subtree GATE (<c>$security-gated:{nodeType}</c>) — the set a
    /// target path is matched against to find its nearest gated ancestor. Truncation here loses a
    /// gate's declared PUBLIC surface rather than opening one (the gate only ever ORs Read in), so it
    /// fails closed — but it is still a permission decided on a page, and it is pinned with the rest.
    /// </summary>
    /// <param name="nodeType">The gated node type.</param>
    /// <returns>The query string.</returns>
    public static string GatedNodes(string nodeType) => Global(nodeType, IdentityProjection);

    /// <summary>
    /// A security read anchored to one scope — the root <c>AccessAssignment</c> and <c>_Policy</c>
    /// legs (<see cref="RootAssignments"/>, <see cref="RootPolicy"/>). Anchored reads are served by
    /// a single schema's delegate; they are stamped like every other fold read because the
    /// completeness rule in the class remarks does not depend on WHERE a read is served.
    /// </summary>
    /// <param name="query">The anchored query string.</param>
    /// <returns>The query string, stamped as an enumeration.</returns>
    public static string Scoped(string query) => Enumeration(query);

    /// <summary>
    /// The ROOT scope's satellite namespace for grants — a root-scope <c>AccessAssignment</c> lives
    /// at <c>_Access/{id}</c>. A <c>_</c>-prefixed first segment is not a partition: the Postgres
    /// router resolves it ONLY through the REGISTERED global-satellite definitions
    /// (<c>DefaultPartitionProvider</c> registers <c>_Access</c> with schema <c>system_access</c>),
    /// so a read anchored here is served by that ONE schema and never by the cross-schema fan-out.
    /// </summary>
    public const string RootAccessNamespace = "_Access";

    /// <summary>
    /// The ROOT scope's policy node — <c>{scope}/_Policy</c> with the empty root scope, i.e. the
    /// node whose path is exactly <c>_Policy</c>.
    /// </summary>
    public const string RootPolicyPath = "_Policy";

    /// <summary>
    /// Every root-scope <c>AccessAssignment</c> (<c>$security-access:</c>) — the grants that apply
    /// across every partition (platform-wide viewer / admin). Anchored on
    /// <see cref="RootAccessNamespace"/>, which the router pins to the registered
    /// <c>system_access</c> schema: one schema, not 199.
    /// </summary>
    public static string RootAssignments
        => Scoped($"namespace:{RootAccessNamespace} "
            + $"nodeType:{SecurityCollections.AccessAssignmentNodeType} {ContentProjection}");

    /// <summary>
    /// The root-scope <c>_Policy</c> (<c>$security-policy:</c>), read as the node at exactly
    /// <see cref="RootPolicyPath"/>.
    ///
    /// <para>🚨 <b>Why <c>path:_Policy</c> and not <c>namespace: id:_Policy</c></b> (issue #2194,
    /// measured on memex-cloud 2026-09-02). The two name the SAME node — a namespace of <c>""</c>
    /// plus an id of <c>_Policy</c> IS the path <c>_Policy</c> — but the old spelling carried no
    /// path, so the Postgres router had no first segment to route on and answered it with a
    /// <c>UNION ALL</c> over every partition schema: 179 <c>[CrossSchema] SLOW</c> lines in five
    /// minutes (<c>nodeType:PartitionAccessPolicy path:- scope:Children</c>), each 1–4 s of
    /// <c>LWLock/LockManager</c> contention shared with every other query on the database — and
    /// every one of them necessarily EMPTY: an unregistered <c>_</c>-prefixed first segment is
    /// unroutable, so no write can ever land a root <c>_Policy</c> row in Postgres in the first
    /// place. The anchored spelling gives the router a concrete first segment, so it never fans
    /// out: today it answers empty (no schema is registered for <c>_Policy</c>), and registering
    /// <c>_Policy</c> as a global satellite the way <c>_Access</c> is would make it pinned WITHOUT
    /// touching this query. In-memory and static-node providers answer both spellings identically.</para>
    /// </summary>
    public static string RootPolicy
        => Scoped($"path:{RootPolicyPath} "
            + $"nodeType:{SecurityCollections.PartitionAccessPolicyNodeType} {ContentProjection}");

    /// <summary>
    /// Every <c>AccessAssignment</c> in ONE partition — the grants for every scope on any path
    /// rooted there, in one anchored read.
    ///
    /// <para>🚨 <b>Why the PARTITION and not the scope</b> (issue #3093). A grant lives at
    /// <c>{scope}/_Access/{id}</c>, and every scope on a path's chain except the root is a PREFIX
    /// of that path — so all of them live in the path's own partition, which is where
    /// <c>_Access</c> is stored (one schema per partition; the <c>_Access</c> segment routes to
    /// that schema's <c>access</c> table). Asking per SCOPE therefore multiplies one partition's
    /// read by the depth of the path AND by how many paths are checked: a node's own path is
    /// always the LEAF of its own chain, so every node ever permission-checked minted its own
    /// live <c>$security-access:{path}</c> query. Measured on this tree: filtering a 4-node
    /// listing opened 13 security queries, a 32-node listing 69 — exactly +2 per node, and the
    /// population never falls below the stream cache's idle window.</para>
    ///
    /// <para>The verdict is unchanged because it was never a function of WHICH scopes were read:
    /// <c>ComputeScopeRoles</c> buckets whatever it is given by each node's own namespace, and
    /// <c>ComputeRoleState</c> then consults only the scopes on the target path's chain. A
    /// partition-wide read is a strict SUPERSET of the per-scope walk, so no grant and no DENY
    /// can be lost — the direction that matters, since a short read in this fold reads as
    /// "denied" (see the class remarks and #2011).</para>
    ///
    /// <para>Anchored by <c>path:</c>, which pins the partition through its first segment. That is
    /// also what makes the Admin partition work without a special case: <c>Admin</c> is excluded
    /// from <c>searchable_schemas</c>, so a namespace-only read never reached <c>admin.access</c>
    /// and platform-admin grants silently never loaded.</para>
    /// </summary>
    /// <param name="partition">The partition (first path segment) to read.</param>
    /// <returns>The query string.</returns>
    public static string PartitionAssignments(string partition)
        => Enumeration($"path:{partition} scope:descendants "
            + $"nodeType:{SecurityCollections.AccessAssignmentNodeType} {ContentProjection}");

    /// <summary>
    /// Every <c>_Policy</c> node in ONE partition — the <see cref="PartitionAssignments"/> twin for
    /// <c>PartitionAccessPolicy</c>, anchored and read for the same reason.
    /// </summary>
    /// <param name="partition">The partition (first path segment) to read.</param>
    /// <returns>The query string.</returns>
    public static string PartitionPolicies(string partition)
        => Enumeration($"path:{partition} scope:descendants id:_Policy "
            + $"nodeType:{SecurityCollections.PartitionAccessPolicyNodeType} {ContentProjection}");

    /// <summary>
    /// The root-scope <c>AccessAssignment</c>s naming ONE subject — <see cref="RootAssignments"/>
    /// narrowed to <paramref name="accessObject"/>, served by the same single registered schema.
    ///
    /// <para>One of the three legs of the SIGN-IN role fold (#3202): a user's platform roles are the
    /// grants on the ROOT scope, on the <c>Admin</c> partition, and on their OWN partition — three
    /// pinned homes, read with <see cref="PartitionAssignmentsFor"/> for the latter two. The read
    /// used to be <c>nodeType:AccessAssignment content.accessObject:"{user}" scope:subtree</c>, no
    /// partition named: a 199-schema UNION on every request, and — once the storage layer stopped
    /// serving unanchored queries — a refusal that answered 503 to every signed-in user.</para>
    /// </summary>
    /// <param name="accessObject">The subject (user id) whose root grants to read.</param>
    /// <returns>The query string.</returns>
    public static string RootAssignmentsFor(string accessObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessObject);
        return Scoped($"namespace:{RootAccessNamespace} "
            + $"nodeType:{SecurityCollections.AccessAssignmentNodeType} "
            + $"content.accessObject:\"{accessObject}\" {ContentProjection}");
    }

    /// <summary>
    /// The <c>AccessAssignment</c>s in ONE partition naming ONE subject —
    /// <see cref="PartitionAssignments"/> narrowed to <paramref name="accessObject"/>. Anchored by
    /// <c>path:</c> for the reason given there: the partition IS where every scope of a path keeps
    /// its grants, and <c>Admin</c> is excluded from cross-schema search, so a platform-admin grant
    /// is reachable ONLY through a path-anchored read.
    /// </summary>
    /// <param name="partition">The partition (first path segment) to read.</param>
    /// <param name="accessObject">The subject (user id) whose grants to read.</param>
    /// <returns>The query string.</returns>
    public static string PartitionAssignmentsFor(string partition, string accessObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessObject);
        return Enumeration($"path:{partition} scope:descendants "
            + $"nodeType:{SecurityCollections.AccessAssignmentNodeType} "
            + $"content.accessObject:\"{accessObject}\" {ContentProjection}");
    }

    /// <summary>
    /// Every query shape this class produces, for the completeness test that pins them. A member
    /// added without an entry here is not covered — which is why the test also asserts the fold's
    /// own builders against <see cref="Enumeration"/>.
    /// </summary>
    public static IReadOnlyList<string> AllShapes =>
    [
        Roles,
        Memberships,
        GatedNodes("Store/Plugin"),
        RootAssignments,
        RootPolicy,
        PartitionAssignments("acme"),
        PartitionPolicies("acme"),
        RootAssignmentsFor("rbuergi"),
        PartitionAssignmentsFor("Admin", "rbuergi"),
        PartitionAssignmentsFor("rbuergi", "rbuergi"),
    ];
}

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// The MESH-OWNED (operational) members of a NodeType node's content — the compile bookkeeping the
/// framework itself persists onto the <c>NodeTypeDefinition</c> as types compile (status,
/// timestamps, assembly pointers, source-version maps, release-request triggers). These members are
/// NOT authored content: the repo owns the definition (configuration, sources, description, …); the
/// mesh owns the compile state. Every seam that moves a NodeType node between a git repo and a live
/// mesh must honour that split:
///
/// <list type="bullet">
/// <item><b>Export STRIPS them</b> (<see cref="StripOperational"/>) — a repo file must not carry a
/// stale compile verdict into git.</item>
/// <item><b>Every UPSERT preserves the live node's values</b> (<see cref="PreserveLiveOperational"/>,
/// applied by the owner inside <c>CreateOrUpdateNodeRequest</c>'s merge) — no writer may regress
/// live compile state to whatever stale copy it happens to hold, whether that is a repo file's
/// embedded verdict (the "stale green" that claims a weeks-old assembly is current, which parks the
/// type when the cache is cold) or a syncing client's eventually-consistent snapshot (which lags the
/// compile pipeline and reverts a healthy type to its previous state — memex 2026-08-02).
/// 🚨 Callers must NOT pre-apply this against their own snapshot: the rule is enforced where the
/// answer is authoritative, and a client-side copy can only ever be as fresh as the index it read.</item>
/// <item><b>Change detection IGNORES them</b> (<see cref="PartitionSourceFingerprint"/>) — a node
/// differing only in bookkeeping has not changed.</item>
/// </list>
///
/// <para>🚨 One member is mesh-written and still must NOT be preserved on import, because dropping
/// it is how it CLEARS — see <see cref="StrippedButNotPreserved"/>. Those are stripped by the first
/// and third rules and skipped by the second, so the strip paths use the UNION and the preserve
/// path uses <see cref="MemberNames"/> alone.</para>
///
/// <para>This is the transitional ownership rule until the compile state moves off the
/// NodeTypeDefinition entirely (into the compile activity / a <c>_Compile</c> satellite the sync
/// never touches); once that lands this class keeps legacy repo files and stored nodes honest.</para>
/// </summary>
public static class NodeTypeOperationalContent
{
    /// <summary>Whether <paramref name="node"/> is a type-definition node — the one node type whose
    /// content carries the operational members.</summary>
    public static bool IsNodeTypeNode(MeshNode? node) =>
        string.Equals(node?.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal);

    /// <summary>
    /// The operational members by their serialized (camelCase) names, matched CASE-INSENSITIVELY:
    /// stored content is camelCased under the hub's naming policy, but writers fall back to the
    /// PascalCase property name when a hub carries no policy (see <c>StampReleaseRequest</c>), so
    /// both casings denote the same member. Pinned against the <c>NodeTypeDefinition</c> record by
    /// <c>NodeTypeOperationalContentTest</c> in the Graph test suite, so the list cannot silently
    /// drift from the record.
    ///
    /// <para>🚨 In BOTH directions since #4480, and the reverse one is the direction that loses
    /// data. <c>MemberNames ⊆ the record</c> catches an entry naming no property; it is
    /// structurally blind to a runtime-state PROPERTY missing from here, which is what leaks the
    /// mesh's own measurements into a repo file and lets a file overwrite them on the way back —
    /// four were missing that way. So the guard is now three: that inclusion, the reverse one over
    /// the control plane's naming convention, and a PARTITION that classifies every serialised
    /// member of the record as repo-authored, mesh-owned-and-masked, or (with its reason)
    /// stripped-but-never-preserved. Only the partition sees a member spelled outside the
    /// convention — <c>dispatchedBuildInputs</c> was exactly that. See
    /// <c>Doc/Architecture/NodeTypeMemberOwnership</c>.</para>
    ///
    /// <para>🚨 The case-insensitive comparer is load-bearing TWICE: for the JSON paths above, and
    /// for <see cref="WithTypedMembersReset"/>, which matches CLR PascalCase property names against
    /// these camelCase entries. Tightened to <c>StringComparer.Ordinal</c>, every JSON-shaped test
    /// would still pass while the typed import path silently stripped nothing.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> MemberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "compilationStatus",
        "compilationError",
        "compilationDiagnostics",
        "lastCompileStartedAt",
        "lastCompileSucceededAt",
        "lastCompiledVersion",
        "lastCompilationActivityPath",
        "latestReleasePath",
        "requestedReleasePath",
        "requestedReleaseAt",
        "requestedReleaseForce",
        "requestedReleaseBy",
        "lastReleaseRequestHandledAt",
        "releaseNotes",
        "latestAssemblyCollection",
        "latestAssemblyPath",
        // #4480 — the third member of the assembly triple, and the only one that is an IDENTITY:
        // the MVID of the bytes the last successful build PRODUCED (#2471). Operational for the
        // same reason the collection/path pair is, and for one sharper one: bind time compares it
        // against the MVID of the bytes actually served, so an authored value forges a MATCH and
        // turns off the stale-build detector that exists to catch a portal serving stale compiled
        // code while reporting Ok — or forges a MISMATCH and refuses a correct bind. Export leaving
        // it behind was incoherent on its own terms: the file then named bytes by an identity that
        // exists nowhere on the importing mesh, while carrying no path to them.
        "latestAssemblyMvid",
        "compiledSources",
        // #4480 — the deployment's installed-MODULE fingerprint the assembly was compiled under
        // (#1644/#1664). Operational for the same reason compiledFrameworkVersion is, and it
        // DECIDES: HasUsableBuild invalidates a build stamped with a different non-null hash than
        // the live set, so an authored hash that happens to match the importing deployment declares
        // a FOREIGN build usable and suppresses the recompile a module update requires. The same
        // class of forgery the adoptedSourceFingerprint entry names.
        "compiledModulesHash",
        // #4480 — the per-type DEPENDENCY RECORD the assembly was compiled with (#1707 slice 2).
        // Operational for the same reason compiledModulesHash is, and it decides in one more place:
        // PrebuiltAssemblySeeder.IsAlreadyAdopted compares the LIVE stamp against the BUNDLE's
        // record, so an authored record matching the bundle makes a FRESH install read as
        // already-adopted — the bytes are never seeded and the type parks on a stamp nobody earned.
        "compiledDependencies",
        "currentSourceVersions",
        // #1834 — the adopter's REQUEST that the owner stamp compiledSources from its own
        // currentSourceVersions. Operational for the same reason both of those are, and for one
        // sharper one: an authored value would ask the owner to re-stamp a compile's source
        // snapshot from the live set, silently suppressing a needed rebuild.
        "requestedSourceStampAt",
        // #2813 — build PROVENANCE. Operational for the same reason the verdict fields are:
        // an authored value would forge the very claim these exist to expose (an adopted
        // assembly asserting it was checked against source nobody compared it to).
        "adoptedSourceFingerprint",
        "currentSourceFingerprint",
        "buildProvenance",
        // #4280 — the source paths the adopted bytes were built from, i.e. what makes a differing
        // live fingerprint a measurement rather than an install in flight. Operational for the
        // same reason the fingerprint is: an authored list would hold an adoption unjudged.
        "adoptedSourcePaths",
        // #4280 — the include halves of the same witness: what the bundle was compiled with, and
        // what the sources watcher resolved as present. Operational for the same reason.
        "adoptedSourceIncludes",
        "currentSourceIncludes",
        // #3583 — the two module versions the compatibility rule compares. Operational for the
        // same reason the fingerprints are: an authored adopted version would let a repo file
        // declare its own build compatible.
        "adoptedModuleVersion",
        "currentModuleVersion",
        "compiledFrameworkVersion",
        // #1793 — the inputs the standing FAILURE verdict was formed from. Operational for the
        // same reason compiledFrameworkVersion is, and for one sharper one: an authored token that
        // happened to match this deployment's live inputs would SUPPRESS the one automatic retry a
        // never-compiled failure gets. Stripped on export, preserved from the live node on import.
        "failedBuildInputs",
        // #3903 — the declared source queries that matched NOTHING when the standing failure was
        // recorded. Operational for the same reason failedBuildInputs is: it is a measurement of
        // THIS mesh's content (which nodes a query matches here), so an authored copy would assert
        // a finding about a partition it was never taken on — and the empty list is the shape that
        // reads as "checked, all present".
        "failedSourceQueries",
        // #4469 — the source nodes an IMPORT recorded as refused that explain the standing
        // failure's unresolved names. Operational for exactly the reason failedSourceQueries is,
        // and for a sharper one: it is a measurement taken against THIS mesh's own import
        // bookkeeping, so an authored copy would accuse an import that never ran here — the
        // unfounded accusation the whole mechanism exists to refuse. Stripped on export, preserved
        // from the live node on import.
        "compilationImportRefusals",
        // #4480 — the build-inputs token the IN-FLIGHT compile was dispatched for (#2544), cleared
        // by every terminal write-back. Operational for the same reason failedBuildInputs is, and
        // it is the member that proved a naming convention cannot be the guard: it is spelled
        // outside the compile/release control plane's prefixes, so nothing ever named it as runtime
        // state and nothing noticed it was missing from here. An authored token matching what a
        // live request resolves to makes that request read as ALREADY IN FLIGHT and be CONSUMED —
        // absorbed against a compile nobody dispatched, so the release it asked for is simply lost.
        "dispatchedBuildInputs",
    };

    /// <summary>
    /// 🚨 MESH-WRITTEN, STRIPPED wherever a node becomes (or is compared as) a FILE — and
    /// deliberately NOT PRESERVED from the live node on import. The THIRD ownership bucket (#4480),
    /// and the one that needs a reason per entry, because it is the one place the two halves of the
    /// seam rule come apart.
    ///
    /// <list type="bullet">
    ///   <item><c>NodeTypeDefinition.PendingRetirement</c> — stamped by a
    ///     repository-driven import when the repo RETIRED a type that still has live instances,
    ///     through the probe's own <c>stream.Update</c> (never an upsert), and NOTHING in
    ///     <c>src/</c> ever writes null back to it. The one thing that clears it is the repo
    ///     shipping the type AGAIN: an upsert replaces the node's content wholesale, so a member
    ///     the live node holds and the file does not simply goes away. Put it in
    ///     <see cref="MemberNames"/> and the live value would win on every import — a re-shipped
    ///     type would stay marked retired forever, and the bake gate reads a stamped type's compile
    ///     failure as <c>Retired</c>, i.e. as a verdict that must NOT hold a rollout. But it is
    ///     still runtime state, so a FILE must never carry it: the export strips it like everything
    ///     else the mesh owns, and an authored value never lands.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> StrippedButNotPreserved =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "pendingRetirement");

    /// <summary>
    /// Everything a repo FILE must not carry — <see cref="MemberNames"/> plus
    /// <see cref="StrippedButNotPreserved"/>. This is the set the strip paths and the
    /// change-detection token use; the PRESERVE half uses <see cref="MemberNames"/> alone, and that
    /// asymmetry IS the third bucket.
    /// </summary>
    private static readonly IReadOnlySet<string> FileExcludedMembers =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            MemberNames.Concat(StrippedButNotPreserved).ToArray());

    /// <summary>
    /// The node with the operational members REMOVED from its content — the shape a repo file (and
    /// the change-detection token) uses. Non-NodeType nodes, non-object content, and content that
    /// carries none of the members pass through as the SAME instance (no reshaping).
    /// </summary>
    public static MeshNode StripOperational(MeshNode node, JsonSerializerOptions options)
    {
        if (!IsNodeTypeNode(node) || ContentObject(node, options) is not { } content)
            return node;
        var removed = false;
        foreach (var key in content.Select(member => member.Key).Where(FileExcludedMembers.Contains).ToArray())
            removed |= content.Remove(key);
        return removed ? node with { Content = content } : node;
    }

    /// <summary>
    /// <see cref="StripOperational"/> for the IMPORT seams (#3474): the same members removed, but
    /// the content comes back in the SHAPE it arrived in — a raw <see cref="JsonElement"/> stays a
    /// raw element, a typed record stays that type with the members reset to their defaults. Export
    /// keeps <see cref="StripOperational"/>, which serialises the result at once and for which a
    /// <see cref="JsonObject"/> IS the file. An import hands the node on to a pipeline that tells a
    /// typed definition from a raw element apart at every step — the installer's unchanged-check
    /// compares two typed definitions on their authored members and aligns a raw element to its
    /// typed peer, <c>ImportWriteOrder</c> reads the discriminator, the bake reads the record — and
    /// none of it knows a <see cref="JsonObject"/>: handed one, a re-install of an unchanged snapshot
    /// read as "changed" and rewrote the type (the gate's idempotence check), and the mesh-driven
    /// bake saw an empty dependency record. Returns the same instance when nothing is removed.
    /// </summary>
    public static MeshNode WithoutOperational(MeshNode node, JsonSerializerOptions options)
    {
        if (!IsNodeTypeNode(node) || node.Content is null)
            return node;
        switch (node.Content)
        {
            case JsonElement:
            {
                var stripped = StripOperational(node, options);
                return ReferenceEquals(stripped, node)
                    ? node
                    : node with { Content = JsonSerializer.SerializeToElement(stripped.Content, options) };
            }
            case JsonNode:
                return StripOperational(node, options);
            default:
                return WithTypedMembersReset(node, options);
        }
    }

    /// <summary>
    /// The typed half of <see cref="WithoutOperational"/>: a record clone with every operational
    /// property reset to its default (null, false, zero) and any extension-data entry of an
    /// operational name dropped. Reflection, because this assembly cannot name the definition type;
    /// the member list is the SAME UNION the JSON strip uses — <see cref="MemberNames"/> plus
    /// <see cref="StrippedButNotPreserved"/> — matched case-insensitively against the property
    /// names. 🚨 The union, not the mask: this is a STRIP, and narrowing it to
    /// <see cref="MemberNames"/> would let a typed file keep a member the JSON path removes, which
    /// is a difference no caller can see. Content that is not a record (no <c>&lt;Clone&gt;$</c>)
    /// falls back to the JSON shape.
    /// </summary>
    private static MeshNode WithTypedMembersReset(MeshNode node, JsonSerializerOptions options)
    {
        var typed = node.Content!;
        var type = typed.GetType();
        var clone = type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public);
        if (clone is null)
        {
            // A non-record content type degrades to the JSON shape — the very shape whose
            // downstream cost this method exists to avoid (an unchanged re-install reads as
            // changed, the bake reads an empty record). Leaking the verdict would be worse, so the
            // choice stands; the only definition type today is a record, so this does not fire.
            // If it ever does, give the type a record's copy semantics rather than widening this.
            return StripOperational(node, options);
        }
        object? copy = null;
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.SetMethod is null)
                continue;
            if (FileExcludedMembers.Contains(property.Name))
            {
                var current = property.GetValue(typed);
                var blank = property.PropertyType.IsValueType
                    ? Activator.CreateInstance(property.PropertyType)
                    : null;
                if (Equals(current, blank))
                    continue;
                copy ??= clone.Invoke(typed, null);
                property.SetValue(copy, blank);
            }
            else if (property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null
                     && property.GetValue(typed) is IDictionary<string, JsonElement> extra
                     && extra.Keys.Any(FileExcludedMembers.Contains))
            {
                copy ??= clone.Invoke(typed, null);
                var kept = extra.Where(kv => !FileExcludedMembers.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                property.SetValue(copy, kept.Count == 0 ? null : kept);
            }
        }
        return copy is null ? node : node with { Content = copy };
    }

    /// <summary>
    /// The INCOMING (repo) node with the LIVE node's operational members carried over — the import
    /// ownership rule. Authored members come from the repo; each operational member ends up exactly
    /// as the mesh last wrote it: the live value when the live node has one, ABSENT when it does not
    /// (a stale value embedded in the file never survives, in either direction). Returns the same
    /// instance when nothing would change, so an authored-identical import stays a no-op upsert.
    ///
    /// <para>🚨 <b>Exception, by design: <see cref="StrippedButNotPreserved"/>.</b> Those members are
    /// removed from the incoming node like every other mesh-owned one — a file may not forge one —
    /// but they are NOT copied back from the live node, so they end up ABSENT whatever the live node
    /// holds. That is what clears them: an upsert replaces the content wholesale, and nothing in
    /// <c>src/</c> ever writes null back to a retirement stamp. Read the promise above as "every
    /// member of <see cref="MemberNames"/>", never "every member the mesh writes".</para>
    ///
    /// <para>🚨 <b>A CREATE is an import too.</b> With no live node (<paramref name="live"/> null)
    /// the mesh has written nothing yet, so every operational member must be ABSENT — the file's
    /// verdict is exactly as stale as it is on an update, and a fresh mesh has no live value to
    /// prefer over it. This used to pass the incoming node through untouched, which meant the ONE
    /// path the export-side strip cannot protect (a repo whose files predate it) wrote a
    /// weeks-old <c>compilationStatus: Ok</c> / <c>compiledFrameworkVersion</c> /
    /// <c>latestAssemblyPath</c> / <c>requestedReleaseForce: true</c> as the type's INITIAL live
    /// state on every fresh install. Measured 2026-09-06 on the Education disposable meshes (portal
    /// build bbcb22f25, MeshWeaver.Plugins node files last written by GitSync on 2026-07-18): Store's
    /// five core types framework-stale-kicked into a FORCED live-source compile at every boot (the
    /// stale <c>requestedReleaseForce: true</c> is what #2824 honours), the boot sweep then adopted
    /// the shipped prebuilt over that compile, and every <c>Edu/Exercise</c> instance came up bound
    /// to a foreign <c>latestAssemblyPath</c> and never answered. The rule is applied where a REPO
    /// FILE becomes a node — the installer's <c>AsAuthored</c> (every package file) and GitSync's
    /// <c>ParseFile</c> — plus the installer's direct-to-persistence <c>BulkSave</c>, and here for
    /// any merge asked to create. Deliberately NOT in the owner's generic create handlers: an
    /// in-process creator (a move, a restore, a fixture) legitimately carries compile state, and
    /// the file-backed persistence reads the mesh's OWN nodes back through the same parser
    /// registry — the rule is about files that come from a repo.</para>
    /// </summary>
    public static MeshNode PreserveLiveOperational(
        MeshNode incoming, MeshNode? live, JsonSerializerOptions options)
    {
        if (live is null)
            return WithoutOperational(incoming, options);
        if (!IsNodeTypeNode(incoming)
            || ContentObject(incoming, options) is not { } original)
            return incoming;
        var liveContent = ContentObject(live, options);
        var merged = (JsonObject)original.DeepClone();
        foreach (var key in merged.Select(member => member.Key).Where(FileExcludedMembers.Contains).ToArray())
            merged.Remove(key);
        if (liveContent is not null)
            foreach (var (key, value) in liveContent.Where(member => MemberNames.Contains(member.Key)))
                merged[key] = value?.DeepClone();
        return JsonNode.DeepEquals(merged, original) ? incoming : incoming with { Content = merged };
    }

    /// <summary>
    /// The node's content as a fresh, mutable <see cref="JsonObject"/>, or null when the content is
    /// not object-shaped. Typed content serializes with its CONCRETE runtime type — never the
    /// <c>object</c> overload, whose polymorphic path adopts foreign types into the registry as a
    /// side effect of a read.
    /// </summary>
    private static JsonObject? ContentObject(MeshNode node, JsonSerializerOptions options) =>
        node.Content switch
        {
            null => null,
            JsonObject jo => (JsonObject)jo.DeepClone(),
            JsonNode => null,
            JsonElement je => je.ValueKind == JsonValueKind.Object ? JsonObject.Create(je) : null,
            var typed => JsonSerializer.SerializeToNode(typed, typed.GetType(), options) as JsonObject,
        };
}

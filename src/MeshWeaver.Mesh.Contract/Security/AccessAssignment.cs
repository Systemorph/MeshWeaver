using System.ComponentModel;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for AccessAssignment mesh nodes.
/// Maps a subject (User or Group) to one or more roles at a specific scope.
/// The scope is determined by the node's namespace in the mesh hierarchy.
/// Node ID = {Subject}_Access, so one node per subject per scope.
/// </summary>
public record AccessAssignment
{
    /// <summary>Subject identifier (User or Group path) for this assignment.</summary>
    [MeshNode(AccessSubjectQueries.Users, AccessSubjectQueries.GroupsTemplate)]
    public string AccessObject { get; init; } = "";

    /// <summary>Optional display name for the subject.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Role assignments for this subject at this scope.</summary>
    [MeshNodeCollection("nodeType:Role namespace:\"\"", "nodeType:Role namespace:{node.namespace} scope:selfAndAncestors")]
    public IReadOnlyList<RoleAssignment> Roles { get; init; } = [];
}

/// <summary>
/// A single role assignment within an AccessAssignment.
/// </summary>
public record RoleAssignment
{
    /// <summary>The role identifier (e.g., "Admin", "Editor", "Viewer").</summary>
    public string Role { get; init; } = "";

    /// <summary>True if this assignment denies rather than grants the role.</summary>
    [UiControl(typeof(SwitchControl))]
    public bool Denied { get; init; }
}

/// <summary>
/// Caps effective permissions at a namespace scope for ALL users (including Admins).
/// Each permission can be individually switched off (false = denied at this scope and below).
/// null = inherit from parent scope (default = allowed).
/// Multiple policies accumulate: parent denials carry to descendants.
/// Stored as a MeshNode with nodeType = "PartitionAccessPolicy",
/// id = "_Policy", namespace = target scope.
/// </summary>
public record PartitionAccessPolicy
{
    /// <summary>false = deny Read at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Read { get; init; }

    /// <summary>false = deny Create at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Create { get; init; }

    /// <summary>false = deny Update at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Update { get; init; }

    /// <summary>false = deny Delete at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Delete { get; init; }

    /// <summary>false = deny Comment at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Comment { get; init; }

    /// <summary>false = deny Execute at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Execute { get; init; }

    /// <summary>false = deny Thread at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Thread { get; init; }

    /// <summary>false = deny Api (programmatic/MCP access) at this scope and below. null = inherit (default: allowed).</summary>
    public bool? Api { get; init; }

    /// <summary>
    /// When true, GRANTS Read to ALL users at this scope and below — a public-read
    /// override with precedence over the role-based grant computation (it is ORed in
    /// AFTER the per-user roles ∩ cap, so a user needs no role at this scope to read).
    /// A deeper policy with <c>Read = false</c> suppresses this inherited grant; a
    /// <c>PublicRead = true</c> at that deeper scope grants Read there again. At the
    /// same scope, PublicRead retains precedence over the Read cap.
    /// This is the policy-driven way to publish a read-only catalog (e.g. the built-in
    /// Agent / Model / Documentation namespaces): the owning static node provider seeds
    /// one <c>_Policy</c> with <c>PublicRead = true</c> and the whole subtree becomes
    /// world-readable immediately (static = no synced-query cold-start race). Distinct
    /// from <see cref="Read"/>, which only *caps* (false = deny) and never grants.
    /// </summary>
    public bool PublicRead { get; init; }

    /// <summary>
    /// When true, role assignments from ancestor scopes are discarded at this
    /// namespace boundary. Only roles assigned at this scope or deeper take effect
    /// (still subject to permission caps).
    /// </summary>
    public bool BreaksInheritance { get; init; }

    /// <summary>
    /// Optional mesh path to REDIRECT a viewer to when they lack Read on a node at this scope (or
    /// below) — "if no access, send them here" — instead of showing an access-denied card. Typically a
    /// PUBLIC page (a course cover, a sign-up / paywall). <c>null</c> or empty = show the normal
    /// access-denied. Inherited: the nearest ancestor scope that sets it wins. The redirect fires only
    /// on a REAL denial (never a guess), and never redirects the target itself (or a node under it), so
    /// it cannot loop — see <c>HubPermissionExtensions.GetRedirectOnDenied</c>. Leading '/' is optional.
    /// </summary>
    public string? RedirectOnDenied { get; init; }

    /// <summary>
    /// 🚨 OPT-IN, DEFAULT OFF: let a page whose content stays GATED describe ITSELF in a link
    /// preview — its own name, its own description and its own share image — to whoever holds the
    /// URL. <c>null</c> (the default) or <c>false</c> = a gated page discloses nothing of its own,
    /// which is the behaviour every partition has today and keeps unless someone sets this.
    ///
    /// <para><b>It is NOT a read grant and confers no access.</b> The
    /// <see cref="MeshWeaver.Mesh.Security.AnonymousGate"/> still refuses the page, its BODY is
    /// still never rendered for a logged-out visitor, and the node stays out of the published
    /// surface and <c>/sitemap.xml</c> — those read the gate, not this. What it opens is exactly
    /// three authored strings and one picture: <c>Name</c>, the authored summary
    /// (<c>Description</c>/<c>abstract</c>/<c>tagline</c>/…, never the body), <c>Icon</c>, and the
    /// card drawn from them. To make a page READABLE, use <see cref="PublicRead"/>.</para>
    ///
    /// <para><b>Inherited nearest-scope-first, like <see cref="RedirectOnDenied"/> and unlike the
    /// permission caps.</b> The closest scope that states a value wins — self, then each ancestor up
    /// to root — so a partition root opts its whole subtree in, and any deeper scope opts back OUT
    /// with an explicit <c>false</c>. A policy filed at a single node's own scope therefore governs
    /// that one node. <see cref="BreaksInheritance"/> does NOT apply: it discards inherited ROLE
    /// assignments, and this is not a role.</para>
    ///
    /// <para>🚨 <b>Know what you are opting in before you set it.</b> Every descendant's TITLE and
    /// SUMMARY become readable by anyone who can guess or is given a URL — that is the point of the
    /// flag, and it is a deliberate disclosure, not a side effect. Set it on a partition whose
    /// page names are marketing (a catalog, an offer, a course) and never on one whose names are
    /// the secret (a deal room, a person's files).</para>
    /// </summary>
    // 🚨 The one LABELLED field on this record, and deliberately so rather than a sweep: a label is
    // owed by what a change ADDS. Its siblings render as wordified property names, which is
    // pre-existing and left where it is.
    [Description("Let gated pages here describe themselves in link previews (name, summary, icon)")]
    [Translation("de", "Gesperrte Seiten hier dürfen sich in Link-Vorschauen selbst beschreiben "
                       + "(Name, Kurzbeschreibung, Symbol)")]
    public bool? PublicPreview { get; init; }

    /// <summary>
    /// Computes the permission cap mask from individual switches.
    /// Permissions set to false are removed; null (inherit) and true are kept.
    /// </summary>
    public Permission GetPermissionCap()
    {
        // Start from ALL BITS SET, not Permission.All. A policy only RESTRICTS the permissions
        // it explicitly lists below; it must never implicitly strip a permission outside that
        // list — Permission.All excludes the privileged grants (Sync, Compile) and any future
        // bit, so using it as the base silently capped Compile/Sync out of every effective set.
        var cap = (Permission)~0;
        if (Read == false) cap &= ~Permission.Read;
        if (Create == false) cap &= ~Permission.Create;
        if (Update == false) cap &= ~Permission.Update;
        if (Delete == false) cap &= ~Permission.Delete;
        if (Comment == false) cap &= ~Permission.Comment;
        if (Execute == false) cap &= ~Permission.Execute;
        if (Thread == false) cap &= ~Permission.Thread;
        if (Api == false) cap &= ~Permission.Api;
        // 🚨 Compile (create a NodeType release) and Sync (static-repo overwrite) are CREATE-class
        // privileged WRITES — there is no per-policy flag for them, so they ride the ~0 base. A
        // partition that denies Create must deny them too: otherwise an Admin/Editor (whose ROLE
        // grants Compile) would retain release-creation on a READ-ONLY partition (Doc / Agent / Role),
        // writing a Release node the policy forbids. Gating on the Create flag keeps them on writable
        // policies (the reason the base is ~0, not Permission.All) while capping them on read-only ones.
        // On a read-only partition the legitimate compile runs as System, never the user
        // (NodeTypeReleaseGateTest.SystemCompile_FillsCache_OnReadOnlyPartition). Without this, the 16
        // PartitionAccessPolicy/StaticNamespacePolicy "capped to Read…" tests see Compile leak through.
        if (Create == false) cap &= ~(Permission.Compile | Permission.Sync);
        return cap;
    }
}

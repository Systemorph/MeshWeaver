using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Seeds the dedicated top-level <c>Feedback</c> space as a static-repo import source, so the whole
/// feature provisions automatically on every instance (no manual create, no first-run bootstrap).
/// Ships two things:
/// <list type="bullet">
///   <item>the <b>space root</b> — a Space landing page that lists the feedback filed into it; and</item>
///   <item>one <b>AccessAssignment</b> granting the <see cref="WellKnownUsers.Public"/> subject the
///     built-in <c>Contributor</c> role. Granting <c>Public</c> folds into <em>every</em> authenticated
///     user in <c>PermissionEvaluator</c> — so all users (and therefore <b>all platform admins</b>, who
///     are not data superusers and would otherwise be locked out of this partition) can read the board
///     and file feedback, without being able to edit or delete other people's entries.</item>
/// </list>
/// <para>🚨 <see cref="SyncMode"/> is <see cref="PartitionSyncMode.Additive"/> because users FILE their
/// own Feedback nodes into this partition — a re-import must NEVER prune them; only a seed node this
/// build previously shipped and has since dropped is removed.</para>
/// </summary>
public sealed class FeedbackStaticRepoSource : IStaticRepoSource
{
    /// <summary>The dedicated Feedback space (top-level partition).</summary>
    public const string PartitionName = "Feedback";

    /// <inheritdoc />
    public string Partition => PartitionName;

    /// <inheritdoc />
    // The seed nodes carry no meaningful version → fingerprint on content (re-imports when the seed changes).
    public bool Versioned => false;

    /// <inheritdoc />
    // 🚨 Additive — users file feedback here; never prune their nodes on re-import.
    public PartitionSyncMode SyncMode => PartitionSyncMode.Additive;

    /// <inheritdoc />
    public IReadOnlyList<MeshNode> EnumerateSourceNodes() =>
    [
        new MeshNode("Public_Access", $"{PartitionName}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "All users — Contributor",
            MainNode = PartitionName,          // MUST equal the scope, or the grant is ignored
            State = MeshNodeState.Active,
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All users",
                Roles = [new RoleAssignment { Role = "Contributor" }],
            },
        },

        // 🔐 Scope the public Contributor grant OUT of the access-config subtree. Contributor is
        // inheritable, so without this the grant would also give every user Create on
        // "Feedback/_Access" — and creating an AccessAssignment only needs Create on the parent, so a
        // user could self-escalate by writing "Feedback/_Access/{me}_Access" granting themselves Admin.
        // BreaksInheritance discards ancestor roles AT this scope, so nobody reaches "Feedback/_Access"
        // via the Public grant: users can create feedback ENTRIES ("Feedback/{id}") but not touch the
        // access config. (System import bypasses permissions, so re-seeding still works.)
        new MeshNode("_Policy", $"{PartitionName}/_Access")
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Feedback access — locked",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy { BreaksInheritance = true },
        },
    ];

    /// <inheritdoc />
    public MeshNode? PartitionRoot => new(PartitionName)
    {
        Name = "Feedback",
        NodeType = "Space",
        Icon = "📣",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = WelcomeMarkdown },
    };

    /// <summary>The Feedback space landing page — a short intro plus the live contents catalog.</summary>
    public const string WelcomeMarkdown = """
        Feedback filed through the `/feedback` skill lands here — one entry per submission, each
        recording the page the user was on and who they are, so the team can act on it.

        Anyone can add feedback; browse what has come in below.

        ## Contents

        @@("area/Search")
        """;
}

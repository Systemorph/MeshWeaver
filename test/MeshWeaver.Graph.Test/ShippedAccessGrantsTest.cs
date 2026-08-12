using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A GRANT COMMITTED TO THIS REPOSITORY CAN NEVER CONFER WRITE — the repository itself is
/// GitSynced, so every <c>_Access</c> node it ships lands on a SYSTEM-OWNED partition.
///
/// <para><b>What went wrong (#1245).</b> <c>samples/Graph/Data</c> shipped 75 write-conferring
/// <c>AccessAssignment</c> files (Admin/Editor/PlatformAdmin, plus three naming a role — "Reader" —
/// that does not exist and therefore counts as write by <see cref="AccessAssignmentGuard"/>'s
/// fail-closed allowlist). On memex-cloud the MeshWeaver repo is synced into the <c>MeshWeaver</c>
/// partition, which carries <c>MeshWeaver/_GitSync</c>, so
/// <see cref="AccessAssignmentGuard.IsForbiddenOnSystemOwned"/> refused every single one —
/// 75 <c>fail:</c> lines in 0.36 s on every sync, and the import completed as
/// <c>ImportedWithErrors</c> forever after.</para>
///
/// <para><b>Why the data is wrong and the guard is right.</b> A repo-committed privileged grant on
/// a system-owned partition is unsatisfiable BY DESIGN, not merely refused: even if the write were
/// let through, <c>SystemOwnedAccessRetractionHandler.RetractAll</c> applies the identical
/// predicate and deletes it the next time the sync is (re)wired. There is no configuration in which
/// such a file works — so it must not be committed.</para>
///
/// <para><b>Where write access actually comes from.</b> Platform admin is
/// <c>Auth:GlobalAdmins</c> → <c>GlobalAdminSeed</c>, which writes the <c>Admin/_Access</c> grant at
/// runtime (AGENTS.md → "Global admin = admin on the Admin partition"). Per-space write is an
/// entitlement granted through the access UI on a live mesh. Neither is a file in this tree.</para>
///
/// <para>Read-only grants (<c>Viewer</c>, <c>Commenter</c>) stay legal and are how the samples
/// declare who may SEE a demo space — those are exactly the grants the guard waves through.</para>
/// </summary>
public class ShippedAccessGrantsTest
{
    private const string AccessFolder = "_Access";

    /// <summary>The roles the mesh actually defines. Anything else is a typo, and the guard's
    /// allowlist treats it as write-conferring — fail-closed, which is why "Reader" was refused.</summary>
    private static readonly string[] DefinedRoles =
    [
        Role.Admin.Id, Role.Editor.Id, Role.Viewer.Id, Role.Commenter.Id, Role.PlatformAdmin.Id
    ];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>🚨 THE #1245 INVARIANT. Fails on the tree as it shipped before the fix.</summary>
    [Fact]
    public void NoShippedGrantConfersWriteAccess()
    {
        var grants = EnumerateShippedGrants().ToList();

        Assert.True(grants.Count > 0,
            "expected to find the shipped _Access grant files — the scan below must not silently "
            + "match nothing, or this guard would pass on an empty set");

        var offenders = grants
            .Where(g => !string.Equals(g.Assignment.AccessObject, WellKnownUsers.System,
                            StringComparison.OrdinalIgnoreCase)
                        && AccessAssignmentGuard.ConfersWriteAccess(g.Assignment))
            .Select(g => $"  {g.RelativePath}: {g.Assignment.AccessObject} → "
                         + string.Join(", ", g.Assignment.Roles.Select(r => r.Role)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A grant committed to this repository must never confer WRITE. This repo is GitSynced "
            + "(memex-cloud imports it as the 'MeshWeaver' partition, which carries "
            + "'MeshWeaver/_GitSync'), so every _Access node it ships lands on a SYSTEM-OWNED "
            + "partition: AccessAssignmentGuard.IsForbiddenOnSystemOwned refuses the upsert, and "
            + "SystemOwnedAccessRetractionHandler would delete it anyway on the next sync. Use "
            + "Viewer/Commenter for sample visibility; provision platform admin through "
            + "Auth:GlobalAdmins (GlobalAdminSeed), never a committed file. Offending grants:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// A role nobody defines is not a harmless typo: <c>ConfersWriteAccess</c> is an ALLOWLIST, so
    /// an unrecognised role reads as write and is refused on every GitSynced portal — while on a
    /// non-synced portal it resolves to no permissions at all and the grant silently does nothing.
    /// Three <c>FutuRe/*/Analysis</c> grants shipped <c>Reader</c> and lost both ways.
    /// </summary>
    [Fact]
    public void EveryShippedGrantNamesADefinedRole()
    {
        var offenders = EnumerateShippedGrants()
            .SelectMany(g => g.Assignment.Roles
                .Where(r => !DefinedRoles.Contains(r.Role, StringComparer.OrdinalIgnoreCase))
                .Select(r => $"  {g.RelativePath}: role '{r.Role}'"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Every shipped grant must name a role the mesh defines ("
            + string.Join(", ", DefinedRoles)
            + "). An unknown role grants NOTHING on a normal portal and counts as WRITE on a "
            + "GitSynced one (the guard's allowlist is fail-closed). Offending roles:\n"
            + string.Join("\n", offenders));
    }

    // ── the scan ─────────────────────────────────────────────────────────────────────────

    private readonly record struct ShippedGrant(string RelativePath, AccessAssignment Assignment);

    private static IEnumerable<ShippedGrant> EnumerateShippedGrants()
    {
        var root = FindRepoRoot();
        foreach (var file in AccessFolders(root)
                     .SelectMany(d => Directory.EnumerateFiles(d, "*.json"))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;   // not a node file — not this guard's business
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("nodeType", out var nodeType)
                    || nodeType.ValueKind != JsonValueKind.String
                    || !string.Equals(nodeType.GetString(),
                        AccessAssignmentGuard.AccessAssignmentNodeType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object)
                    continue;

                var assignment = content.Deserialize<AccessAssignment>(Json);
                if (assignment is not null)
                    yield return new ShippedGrant(
                        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                        assignment);
            }
        }
    }

    /// <summary>
    /// Every <c>_Access</c> folder in the source tree, found by a PRUNING walk — the excluded
    /// directories are never descended into, which keeps the scan off <c>node_modules</c> and build
    /// output, and (the one that matters for correctness) off the sibling agent worktrees the
    /// primary checkout keeps under <c>.claude/worktrees/</c>. Each of those is a full checkout on
    /// somebody else's branch, so scanning them would make this guard report other people's work in
    /// progress as if it were committed here.
    /// </summary>
    private static IEnumerable<string> AccessFolders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var dir in Directory.EnumerateDirectories(stack.Pop()))
            {
                var name = Path.GetFileName(dir);
                if (IsPruned(name))
                    continue;
                if (name.Equals(AccessFolder, StringComparison.OrdinalIgnoreCase))
                    yield return dir;
                else
                    stack.Push(dir);
            }
        }
    }

    private static bool IsPruned(string directoryName) =>
        directoryName.StartsWith('.')
        || directoryName.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || directoryName.Equals("artifacts", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}

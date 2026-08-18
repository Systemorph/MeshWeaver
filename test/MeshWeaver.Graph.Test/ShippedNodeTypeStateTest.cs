using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A COMMITTED NODE FILE MUST NOT CARRY COMPILE STATE — a NodeType imported with a
/// <see cref="NodeTypeDefinition.CompilationStatus"/> it did not earn on THIS deployment can
/// never compile again.
///
/// <para><b>What went wrong (#1786).</b> <c>samples/Graph/Data</c> shipped 32 NodeType files
/// that had been produced by EXPORTING a running mesh, so each carried the exporting machine's
/// runtime compile record — status, error text, Roslyn diagnostics, source-version snapshots,
/// assembly coordinates. Eight of them carried <c>"compilationStatus": "Error"</c> from a
/// compile that ran on 2026-06-26, and every deployment that imported the tree adopted that
/// verdict verbatim.</para>
///
/// <para><b>Why the state is unrecoverable, not merely stale.</b> Every automatic path that
/// could re-drive a compile is closed to such a node:
/// <list type="bullet">
///   <item>the first-build kickoff requires <c>CompilationStatus is null</c> — an imported
///     status is not null, so it reads as "already attempted here";</item>
///   <item>the recovery kickoff requires <c>Compiling</c>;</item>
///   <item>the framework-stale kickoff requires <c>LatestAssemblyCollection</c>,
///     <c>LatestAssemblyPath</c> AND <c>CompiledFrameworkVersion</c> — a failed compile stamps
///     none of them (<c>NodeTypeCompilationHelpers.ApplyCompileFailure</c>);</item>
///   <item>the release-request watcher requires <c>RequestedReleaseAt &gt;
///     LastReleaseRequestHandledAt</c> — and the export baked the two EQUAL.</item>
/// </list>
/// So the node parked on a foreign machine's error forever, and no later fix to the code, the
/// sources, or the framework could reach it. The committed error text for
/// <c>Northwind/AnalyticsCatalog</c> named <c>OrderViews</c>/<c>SalesViews</c> — identifiers
/// that occur nowhere in the configuration it shipped alongside; the configuration had long
/// since been corrected, and the correction could never take effect.</para>
///
/// <para><b>#1793 narrowed that, and this guard is why it still matters.</b> A never-compiled
/// failure now earns ONE automatic re-drive whenever the live compile inputs differ from the ones
/// its verdict was formed under (<see cref="NodeTypeDefinition.FailedBuildInputs"/>). An authored
/// file that carries a <c>failedBuildInputs</c> token matching the importing deployment would
/// SUPPRESS exactly that retry — so the banned set covers it too, and the invariant below is
/// unchanged: a committed node file carries no compile state at all.</para>
///
/// <para><b>Why the check is fail-closed by NAME.</b> The banned set is derived from
/// <see cref="NodeTypeDefinition"/> by reflection over the control plane's naming convention
/// rather than hard-coded, so a compile-state member added later is banned the day it is added
/// — a hand-maintained list would silently stop covering the thing it exists to cover.</para>
///
/// <para>The correct authored shape is the one <c>src/MeshWeaver.Documentation/Data/**</c>
/// already uses: configuration, sources, description, display metadata — and nothing the
/// runtime owns. The runtime fills the rest in, per deployment, and keeps it: the imports do
/// NOT clobber a live node's compile record with an authored file that omits it.</para>
/// </summary>
public class ShippedNodeTypeStateTest
{
    /// <summary>
    /// The prefixes the compile/release control plane names its state with. Every
    /// <see cref="NodeTypeDefinition"/> member matching one is written by the runtime and must
    /// never be authored into a file. Nothing authored starts with any of them
    /// (<c>Configuration</c>, <c>ContentCollections</c>, <c>CreatableTypes</c> are the near
    /// misses, and none of them match).
    /// </summary>
    private static readonly string[] RuntimeStatePrefixes =
    [
        "Compilation",       // Status, Error, Diagnostics
        "Compiled",          // Sources, FrameworkVersion, ModulesHash, Dependencies
        "LastCompil",        // LastCompileStartedAt/SucceededAt, LastCompiledVersion, LastCompilationActivityPath
        "LastRelease",       // LastReleaseRequestHandledAt
        "LatestAssembly",    // Collection, Path
        "LatestRelease",     // LatestReleasePath
        "RequestedRelease",  // Path, At, Force, By
        "CurrentSource",     // CurrentSourceVersions
        "Failed",            // FailedBuildInputs (#1793) — the standing failure verdict's inputs
    ];

    /// <summary>The banned members as they appear in JSON (camelCase), derived from the type.</summary>
    private static readonly IReadOnlySet<string> BannedMembers =
        typeof(NodeTypeDefinition).GetProperties()
            .Select(p => p.Name)
            .Where(name => RuntimeStatePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            .Select(JsonNamingPolicy.CamelCase.ConvertName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>🚨 THE #1786 INVARIANT. Fails on the tree as it shipped before the fix.</summary>
    [Fact]
    public void NoShippedNodeTypeCarriesRuntimeCompileState()
    {
        var definitions = EnumerateShippedNodeTypes().ToList();

        Assert.True(definitions.Count > 0,
            "expected to find the shipped NodeType files — the scan below must not silently "
            + "match nothing, or this guard would pass on an empty set");

        var offenders = definitions
            .Select(d => (d.RelativePath, Banned: d.Members.Where(BannedMembers.Contains).ToList()))
            .Where(x => x.Banned.Count > 0)
            .Select(x => $"  {x.RelativePath}: {string.Join(", ", x.Banned)}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A committed NodeType file must not carry the runtime's compile state. A node "
            + "imported with a CompilationStatus it did not earn on this deployment is "
            + "unreachable by EVERY automatic compile path — the first-build kickoff needs a "
            + "null status, recovery needs Compiling, the framework-stale kickoff needs assembly "
            + "coordinates a failed compile never writes, and the release watcher needs "
            + "RequestedReleaseAt > LastReleaseRequestHandledAt (an export bakes them equal). It "
            + "parks on a foreign machine's verdict forever. Author only configuration + display "
            + "metadata, as src/MeshWeaver.Documentation/Data/** does, and let each deployment "
            + "compile for itself. Offending files:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The banned set is only as good as its derivation: if a rename or refactor stops the
    /// naming convention from matching, the guard above would quietly check nothing.
    /// </summary>
    [Fact]
    public void RuntimeStateMembersAreDiscoverable()
    {
        Assert.Contains("compilationStatus", BannedMembers);
        Assert.Contains("compiledSources", BannedMembers);
        Assert.Contains("currentSourceVersions", BannedMembers);
        Assert.Contains("latestAssemblyPath", BannedMembers);
        Assert.Contains("lastReleaseRequestHandledAt", BannedMembers);
        Assert.Contains("lastCompilationActivityPath", BannedMembers);
        // #1793: an authored token matching this deployment's live inputs would suppress the one
        // automatic retry a never-compiled failure gets — the exact shape this guard exists for.
        Assert.Contains("failedBuildInputs", BannedMembers);

        // Authored members must NOT be swept up — the guard has to leave real content alone.
        Assert.DoesNotContain("configuration", BannedMembers);
        Assert.DoesNotContain("sources", BannedMembers);
        Assert.DoesNotContain("contentCollections", BannedMembers);
        Assert.DoesNotContain("creatableTypes", BannedMembers);
    }

    // ── the scan ─────────────────────────────────────────────────────────────────────────

    private readonly record struct ShippedNodeType(string RelativePath, IReadOnlyList<string> Members);

    private static IEnumerable<ShippedNodeType> EnumerateShippedNodeTypes()
    {
        var root = FindRepoRoot();
        foreach (var file in NodeFiles(root))
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
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("$type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), nameof(NodeTypeDefinition), StringComparison.Ordinal))
                    continue;

                yield return new ShippedNodeType(
                    Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                    content.EnumerateObject().Select(p => p.Name).ToList());
            }
        }
    }

    /// <summary>
    /// Every <c>.json</c> in the source tree, found by a PRUNING walk — the excluded directories
    /// are never descended into, which keeps the scan off <c>node_modules</c> and build output,
    /// and (the one that matters for correctness) off the sibling agent worktrees the primary
    /// checkout keeps under <c>.claude/worktrees/</c>. Each of those is a full checkout on
    /// somebody else's branch, so scanning them would make this guard report other people's work
    /// in progress as if it were committed here.
    /// </summary>
    private static IEnumerable<string> NodeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return file;
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (!IsPruned(Path.GetFileName(child)))
                    stack.Push(child);
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

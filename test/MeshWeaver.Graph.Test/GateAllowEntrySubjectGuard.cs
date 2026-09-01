using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A KNOWN-DEBT ALLOW ENTRY MUST NAME A SUBJECT THIS REPO STILL SHIPS — an entry whose subject
/// has been deleted, renamed or retyped is invisible to the gate's own ratchet, so it survives
/// forever as evidence of debt that was in fact paid.
///
/// <para><b>What went wrong (#1786, part three).</b> The content gate's ratchet fails a listed
/// entry that starts PASSING (<c>STALE allow entry (now compiles — remove it)</c>), which is what
/// keeps the list shrinking. But that check asks "did this subject pass?", and a subject that no
/// longer EXISTS never passes and never fails — the gate reports it as
/// <c>unverifiable allow entry (check skipped or scope absent this run)</c> and carries on green,
/// deliberately: the tester cannot tell a narrowed stage from a retired subject, and refusing to
/// infer from a check that did not run is the right default THERE.</para>
///
/// <para>It is not the right default HERE, where the whole tree is always staged. When
/// <c>b63fbf057</c>/<c>091a57a6c</c> retired the six
/// <c>FutuRe/{AmericasIns,AsiaRe,EuropeRe}/{LineOfBusiness,TransactionMapping}</c> NodeTypes to
/// plain <c>Markdown</c> nodes, their six <c>compile</c> entries in
/// <c>.github/samples-gate.allow</c> became dead weight naming nothing. Every main run since
/// printed six <c>unverifiable</c> lines and stayed green; the entries read to anyone opening the
/// file as six NodeTypes still failing to compile in production, which is the condition #1786 is
/// about. They were removed only when someone re-measured the mesh by hand.</para>
///
/// <para><b>Why a repo-local guard rather than making <c>unverifiable</c> fail.</b> The tester is
/// shared: satellite repos stage a SUBSET of their tree (one package, the changed packages), so
/// "listed but absent" is legitimately unknowable there and failing it would break them. This repo
/// stages its whole content tree unconditionally — <c>stage-samples-gate.sh</c> copies all eight
/// packages, <c>stage-doc-gate.sh</c> copies the whole Doc tree — so here "absent" means RETIRED,
/// and it can be decided statically, in milliseconds, off the files themselves.</para>
///
/// <para><b>Scope, stated honestly.</b> This decides whether an entry's SUBJECT exists and is still
/// the kind of thing its check applies to. It says nothing about whether the debt is real — that is
/// the gate's job, and the gate's stale-detection already covers the case where the subject exists
/// and now passes. The two are complements: stale-detection catches "fixed", this catches
/// "gone".</para>
/// </summary>
public class GateAllowEntrySubjectGuard
{
    /// <summary>
    /// Checks the gate applies to one NodeType. Immutable and write-once — the collections policy's
    /// sanctioned <c>static readonly</c>: a reserved-word set, never written at runtime.
    /// </summary>
    private static readonly ImmutableHashSet<string> TypeLevelChecks =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "compile", "render", "tests");

    /// <summary>Checks the gate applies to one package as a whole. Same shape, same reason.</summary>
    private static readonly ImmutableHashSet<string> PackageLevelChecks =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "install", "idempotence");

    /// <summary>The <c>for name in A B C; do</c> line that names every package the samples gate stages.</summary>
    private static readonly Regex StagedPackagesPattern =
        new(@"^for name in (?<names>[^;]+); do\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// One gate lane: its ratchet file, the content tree it judges, and the path prefix its staging
    /// script prepends (the Doc tree is staged UNDER <c>Doc/</c>, so a <c>Doc/X</c> scope is the
    /// file <c>X.json</c> in that tree; the samples packages are staged at their own names, so the
    /// prefix there is empty).
    /// </summary>
    private readonly record struct Lane(
        string AllowFile, string TreeRoot, string StagedPrefix, ImmutableHashSet<string> StagedPackages);

    /// <summary>
    /// 🚨 THE INVARIANT. Fails on the tree as it stood before the fix, naming all six retired
    /// FutuRe entries.
    /// </summary>
    [Fact]
    public void EveryAllowEntryNamesASubjectThisRepoStillShips()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var lane in Lanes(root))
        {
            foreach (var (scope, check, line) in ParseAllowFile(Path.Combine(root, lane.AllowFile)))
            {
                var package = scope.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? scope;
                if (!lane.StagedPackages.Contains(package))
                {
                    offenders.Add(
                        $"  {lane.AllowFile}:{line} '{scope} {check}' — package '{package}' is not staged "
                        + $"by this lane, so the check can never run");
                    continue;
                }

                var relative = StripPrefix(scope, lane.StagedPrefix);
                if (TypeLevelChecks.Contains(check))
                {
                    var subject = ResolveNodeFile(Path.Combine(root, lane.TreeRoot), relative);
                    if (subject is null)
                        offenders.Add(
                            $"  {lane.AllowFile}:{line} '{scope} {check}' — no node file under "
                            + $"{lane.TreeRoot} at '{relative}'");
                    else if (NodeContentDiscriminator(subject) is not nameof(NodeTypeDefinition))
                        offenders.Add(
                            $"  {lane.AllowFile}:{line} '{scope} {check}' — "
                            + $"{SourceScanRelative(root, subject)} is not a NodeType any more "
                            + $"(nodeType: {NodeTypeName(subject) ?? "?"})");
                }
                else if (PackageLevelChecks.Contains(check))
                {
                    var directory = relative.Length == 0
                        ? Path.Combine(root, lane.TreeRoot)
                        : Path.Combine(root, lane.TreeRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory))
                        offenders.Add(
                            $"  {lane.AllowFile}:{line} '{scope} {check}' — no package directory at "
                            + $"{lane.TreeRoot}/{relative}");
                }
                else
                {
                    offenders.Add(
                        $"  {lane.AllowFile}:{line} '{scope} {check}' — '{check}' is neither a "
                        + $"type-level [{string.Join(", ", TypeLevelChecks)}] nor a package-level "
                        + $"[{string.Join(", ", PackageLevelChecks)}] check");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A known-debt allow entry names a subject this repo no longer ships. Such an entry can "
            + "never pass and never fail, so the gate's own stale-detection cannot retire it — it "
            + "prints `unverifiable allow entry (check skipped or scope absent this run)` and stays "
            + "green, while the file goes on asserting that a NodeType is still failing to compile "
            + "in production (#1786). Delete the line: the debt it names is gone with its subject. "
            + "If instead you MEANT to keep the subject, the retirement is what needs undoing.\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The guard is only as good as its resolver: if that silently answered "found" (or "not a
    /// NodeType") for everything, the check above would be worthless in one direction or noise in
    /// the other. Pins both answers, and pins the retired-FutuRe shape specifically — those six
    /// files are still in the tree as Markdown, so they are a permanent, self-maintaining witness
    /// that "the file exists" is NOT what this guard accepts.
    /// </summary>
    [Fact]
    public void TheResolverIsNotVacuous()
    {
        var root = FindRepoRoot();
        var lanes = Lanes(root).ToList();
        Assert.Equal(2, lanes.Count);

        var samples = lanes.Single(l => l.AllowFile.EndsWith("samples-gate.allow", StringComparison.Ordinal));
        // The floor is a NON-VACUITY check on the parse, not an inventory: it catches
        // StagedPackagesPattern reading its own input as empty or near-empty. It therefore tracks
        // the staged list and drops when a tree legitimately leaves — Cornerstone moved to
        // MeshWeaver.Plugins with MeshWeaver.Maps, taking the list from 8 to 7. Lowering it in step
        // with a deliberate removal is maintenance; lowering it to silence a parse that broke would
        // be the opposite, which is what the message below is for.
        Assert.True(samples.StagedPackages.Count >= 7,
            "expected stage-samples-gate.sh to name every staged package; parsed "
            + $"{samples.StagedPackages.Count}. If that script's `for name in …; do` line changed "
            + "shape, teach StagedPackagesPattern rather than letting the parse yield nothing — a "
            + "guard that reads its own input as empty is a guard that passes on no evidence.");
        Assert.Contains("FutuRe", samples.StagedPackages);
        Assert.Contains("Northwind", samples.StagedPackages);
        Assert.DoesNotContain("Doc", samples.StagedPackages);
        // Witnesses that the parse reflects the CURRENT script rather than a remembered one: Doc was
        // never staged, and Cornerstone was until it moved out. A parse that answered from a stale
        // copy would still contain it.
        Assert.DoesNotContain("Cornerstone", samples.StagedPackages);

        var tree = Path.Combine(root, samples.TreeRoot);

        // A live NodeType resolves, and resolves AS a NodeType …
        var live = ResolveNodeFile(tree, "FutuRe/LineOfBusiness");
        Assert.NotNull(live);
        Assert.Equal(nameof(NodeTypeDefinition), NodeContentDiscriminator(live!));

        // … the six retired regional copies still have files, and must NOT read as NodeTypes …
        foreach (var retired in new[]
                 {
                     "FutuRe/AmericasIns/LineOfBusiness", "FutuRe/AmericasIns/TransactionMapping",
                     "FutuRe/AsiaRe/LineOfBusiness", "FutuRe/AsiaRe/TransactionMapping",
                     "FutuRe/EuropeRe/LineOfBusiness", "FutuRe/EuropeRe/TransactionMapping",
                 })
        {
            var file = ResolveNodeFile(tree, retired);
            Assert.True(file is not null, $"{retired} should still exist as a node file");
            Assert.NotEqual(nameof(NodeTypeDefinition), NodeContentDiscriminator(file!));
        }

        // … and a scope naming nothing resolves to nothing.
        Assert.Null(ResolveNodeFile(tree, "FutuRe/NoSuchNode"));

        // The Doc lane's prefix is real: `Doc/X` addresses `X` inside the documentation tree.
        var doc = lanes.Single(l => l.AllowFile.EndsWith("doc-gate.allow", StringComparison.Ordinal));
        Assert.Equal("Doc/", doc.StagedPrefix);
        Assert.NotNull(ResolveNodeFile(
            Path.Combine(root, doc.TreeRoot),
            StripPrefix("Doc/Architecture/BusinessRules/Cession", doc.StagedPrefix)));

        // The allow files themselves are REQUIRED inputs of the gate — a missing one is a
        // configuration error there and must not read as "nothing to check" here.
        foreach (var lane in lanes)
            Assert.True(File.Exists(Path.Combine(root, lane.AllowFile)),
                $"{lane.AllowFile} is missing — the gate passes it with --allow and refuses to run "
                + "without it, so an empty ratchet must be spelled as an EMPTY FILE.");
    }

    // ── the scans ────────────────────────────────────────────────────────────────────────

    private static IEnumerable<Lane> Lanes(string root)
    {
        yield return new Lane(
            ".github/samples-gate.allow",
            "samples/Graph/Data",
            string.Empty,
            StagedSamplesPackages(root));
        yield return new Lane(
            ".github/doc-gate.allow",
            "src/MeshWeaver.Documentation/Data",
            "Doc/",
            ImmutableHashSet.Create(StringComparer.Ordinal, "Doc"));
    }

    /// <summary>
    /// The packages <c>stage-samples-gate.sh</c> actually copies — read from the script so the two
    /// cannot drift. A scope under a package the script does not stage is dead in the same way a
    /// deleted node is: nothing ever judges it.
    /// </summary>
    private static ImmutableHashSet<string> StagedSamplesPackages(string root)
    {
        var script = Path.Combine(root, ".github", "scripts", "stage-samples-gate.sh");
        if (!File.Exists(script))
            return ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        var match = StagedPackagesPattern.Match(File.ReadAllText(script));
        return match.Success
            ? ImmutableHashSet.Create(
                StringComparer.Ordinal,
                match.Groups["names"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    }

    /// <summary>
    /// The allow file's own format — <c>&lt;scope&gt; &lt;check&gt; [intermittent]</c>, <c>#</c>
    /// comments, blank lines ignored. Kept as a local parse rather than a reference to
    /// <c>MeshWeaver.PluginTester</c>: this test project references the framework, not the CI tool,
    /// and the format is three tokens.
    /// </summary>
    private static IEnumerable<(string Scope, string Check, int Line)> ParseAllowFile(string path)
    {
        if (!File.Exists(path))
            yield break;
        var number = 0;
        foreach (var raw in File.ReadLines(path))
        {
            number++;
            var hash = raw.IndexOf('#', StringComparison.Ordinal);
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                yield return (parts[0], parts[1], number);
        }
    }

    /// <summary>
    /// The node file a mesh path addresses in a content tree: <c>X/Y.json</c> beside its siblings,
    /// or <c>X/Y/index.json</c> for a root authored as a folder. Null when the path names neither.
    /// </summary>
    private static string? ResolveNodeFile(string treeRoot, string relativePath)
    {
        if (relativePath.Length == 0)
            return null;
        var native = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var sibling = Path.Combine(treeRoot, native + ".json");
        if (File.Exists(sibling))
            return sibling;
        var index = Path.Combine(treeRoot, native, "index.json");
        return File.Exists(index) ? index : null;
    }

    /// <summary>The <c>content.$type</c> discriminator, or null when the file is not a node.</summary>
    private static string? NodeContentDiscriminator(string file) =>
        ReadNodeProperty(file, node =>
            node.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("$type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null);

    /// <summary>The node's declared <c>nodeType</c> — for the diagnostic, so the message says what it became.</summary>
    private static string? NodeTypeName(string file) =>
        ReadNodeProperty(file, node =>
            node.TryGetProperty("nodeType", out var nodeType) && nodeType.ValueKind == JsonValueKind.String
                ? nodeType.GetString()
                : null);

    private static string? ReadNodeProperty(string file, Func<JsonElement, string?> read)
    {
        // File.ReadAllText strips a UTF-8 BOM; several shipped sample nodes carry one, and
        // JsonDocument.Parse would otherwise throw on the first character.
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripPrefix(string scope, string prefix) =>
        prefix.Length > 0 && scope.StartsWith(prefix, StringComparison.Ordinal)
            ? scope[prefix.Length..]
            : prefix.Length > 0 && scope.Equals(prefix.TrimEnd('/'), StringComparison.Ordinal)
                ? string.Empty
                : scope;

    private static string SourceScanRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>THE BYPASS HAS EXACTLY ONE CALLER, AND THIS IS WHAT MAKES THAT TRUE.</b>
///
/// <para><c>CreateOrUpdateNodeRequest.AllowUnresolvableNodeType</c> turns off the update path's
/// NodeType existence check — i.e. it lets one write CREATE the dangling-type condition issue #2993
/// exists to close: an instance whose type resolves to nothing has no per-node hub, so it reads as
/// <c>Unavailable</c> on a timeout, renders empty, and never reaches a verdict, with nothing naming
/// why.</para>
///
/// <para>It exists because a refusal with no exemption breaks the static-repo import: a node that
/// already exists and is retyped to a NodeType this pass cannot put in place first (a cycle, a type
/// from another repo) would be a per-file FAILURE, and <c>Failed &gt; 0</c> holds the caller's git
/// baseline — one such node freezes every later commit of the repo. That is #2556's non-convergent
/// loop, re-created by the fix for #2993.</para>
///
/// <para><b>Why a guard rather than a comment.</b> A <c>bool</c> init property is one keystroke away
/// for anyone whose write is being refused, and "my upsert was rejected" is exactly the moment
/// someone reaches for it. A silent bypass is worse than no validator at all: the refusal at least
/// tells you the type is missing. So the call-site set is pinned here, and a new setter fails CI
/// naming the property — at which point the honest fix is almost always to write the NodeType
/// first.</para>
///
/// <para><b>The guard may only SHRINK.</b> A file that appears here and is not in
/// <see cref="SanctionedFiles"/> is a failure; a sanctioned file that no longer mentions the
/// property is ALSO a failure, because a guard whose subject moved while its expectation did not
/// passes having checked nothing (AGENTS.md, "a gate never tests its own inputs"). Remove a
/// sanctioned entry in the same change that removes its last use.</para>
///
/// <para><c>test/</c> is deliberately out of scope: a test that pins the escape hatch has to be able
/// to take it, and the harm this guards against — a production writer stranding live content — is a
/// property of <c>src/</c>.</para>
/// </summary>
public class UnresolvableNodeTypeBypassGuard(ITestOutputHelper output)
{
    /// <summary>The identifier being ratcheted. Case-sensitive: the C# property, not the parameter
    /// name that carries it into the request.</summary>
    private const string Marker = "AllowUnresolvableNodeType";

    /// <summary>Production roots. A bypass in a sample or a tool would strand real content too.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "tools", "samples"];

    /// <summary>
    /// The complete sanctioned inventory, and what each entry is for:
    /// <list type="bullet">
    ///   <item><c>CreateNodeRequest.cs</c> — DECLARES the property.</item>
    ///   <item><c>MeshExtensions.cs</c> — READS it, in the one gate it disarms, and logs a warning
    ///     naming the path and the type every time it is taken.</item>
    ///   <item><c>StaticRepoImporter.cs</c> — the ONE writer that SETS it, only for a node that
    ///     already exists and whose NodeType this pass provably cannot put in place first, and it
    ///     names every such write in the import activity.</item>
    /// </list>
    /// </summary>
    private static readonly string[] SanctionedFiles =
    [
        "src/MeshWeaver.Graph/StaticRepoImporter.cs",
        "src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs",
        "src/MeshWeaver.Mesh.Contract/MeshExtensions.cs",
    ];

    [Fact]
    public void TheDanglingNodeTypeBypassIsReachableFromNowhereNew()
    {
        var root = SourceScan.FindRepoRoot();
        var found = SourceScan.SourceFiles(root, ScannedRoots)
            .Where(f => File.ReadAllText(f).Contains(Marker, StringComparison.Ordinal))
            // Comments and string literals are blanked so the RULE is measured, not the prose
            // explaining it — every sanctioned site documents itself at length.
            .Where(f => SourceScan.MaskCommentsAndStrings(File.ReadAllText(f))
                .Contains(Marker, StringComparison.Ordinal))
            .Select(f => SourceScan.Relative(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        output.WriteLine($"{Marker} sites: {string.Join(", ", found)}");

        var failures = new List<string>();
        foreach (var file in found.Except(SanctionedFiles, StringComparer.Ordinal))
            failures.Add(
                $"  NEW SITE   {file} — this disarms the NodeType existence check on the update "
                + "path (#2993) and can strand live content with no per-node hub. Write the "
                + "NodeType first; if this really is a new ordering escape hatch, say so in "
                + "Doc/Architecture/DanglingNodeTypes and add the file here in the same change.");
        foreach (var file in SanctionedFiles.Except(found, StringComparer.Ordinal))
            failures.Add(
                $"  STALE      {file} — sanctioned but no longer mentions {Marker}. A guard whose "
                + "subject moved while its expectation did not passes having checked nothing: "
                + "either the site moved (point this guard at it) or the bypass is gone (delete "
                + "this entry, and the property with it).");

        Assert.True(failures.Count == 0,
            $"{Marker} call sites drifted from the sanctioned set:\n{string.Join("\n", failures)}");
    }
}

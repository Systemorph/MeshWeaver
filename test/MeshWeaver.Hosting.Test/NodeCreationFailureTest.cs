using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins how a failed create is CLASSIFIED by its caller.
///
/// <para>🚨 The typed reason used to die at the exception boundary. Both create surfaces mapped
/// <see cref="NodeCreationRejectionReason.NodeAlreadyExists"/> to a bare
/// <see cref="InvalidOperationException"/> whose only record of the reason was English prose — so a
/// caller wanting to tell "this path is taken" from "validation failed" had to parse it. That is
/// what #3407 cost: <c>TryCreateReleaseNode</c> caught every exception into <c>null</c>, a re-cut
/// that collided with its own first attempt in the same second was read as a failure, and the
/// NodeType went on advertising a build no release named.</para>
///
/// <para>Two wordings exist in production (<c>"Node already exists: {path}"</c> and
/// <c>"Node already exists at path: {path}"</c>), and the pre-existing string check in
/// <c>MeshNodeExtensions.IsAlreadyExistsRace</c> matches only the first.</para>
///
/// <para>🚨 <b>What the guards below do and do not cover</b>, stated because I checked rather than
/// assumed. <c>EveryProducersWording_IsMatchedByThePredicate</c> pins that every existing
/// <c>"Node already exists…"</c> literal classifies, so a producer TWEAKING that phrase cannot
/// escape. It does NOT notice a producer that abandons the phrase entirely — reworded to
/// <c>"Duplicate node path:"</c> it stays green, verified. <c>NoHandRolledRejectionMapping</c>
/// covers the realistic form of that: re-introducing the per-surface
/// <c>RejectionReason switch</c> the fix removed. A genuinely NEW create surface that hand-writes a
/// fresh message is caught by neither, and the contract it must follow is simply
/// <see cref="NodeCreationFailure.ToException"/>.</para>
/// </summary>
public class NodeCreationFailureTest
{
    [Fact]
    public void TheTypedReason_SurvivesTheExceptionBoundary()
    {
        var ex = CreateNodeResponse.Fail("nope", NodeCreationRejectionReason.NodeAlreadyExists)
            .ToException("Some/Path");

        Assert.Equal(
            NodeCreationRejectionReason.NodeAlreadyExists,
            ex.Data[NodeCreationFailure.RejectionReasonKey]);
        Assert.True(ex.IsNodeAlreadyExists());
    }

    [Theory]
    [InlineData(NodeCreationRejectionReason.ValidationFailed)]
    [InlineData(NodeCreationRejectionReason.InvalidNodeType)]
    [InlineData(NodeCreationRejectionReason.InvalidPath)]
    [InlineData(NodeCreationRejectionReason.Unknown)]
    public void EveryOtherRejection_IsNotAnAlreadyExists(NodeCreationRejectionReason reason)
    {
        var ex = CreateNodeResponse.Fail("nope", reason).ToException("Some/Path");
        Assert.False(ex.IsNodeAlreadyExists());
    }

    /// <summary>A validation failure must stay an authorization error — the mapping is not
    /// collapsed by carrying the reason alongside it.</summary>
    [Fact]
    public void ValidationFailed_IsStillUnauthorized()
        => Assert.IsType<UnauthorizedAccessException>(
            CreateNodeResponse.Fail("denied", NodeCreationRejectionReason.ValidationFailed)
                .ToException("Some/Path"));

    /// <summary>
    /// 🚨 BOTH production wordings classify, on message alone — the fallback for a producer that
    /// throws directly rather than going through <c>ToException</c>.
    /// </summary>
    [Theory]
    [InlineData("Node already exists: Some/Path")]
    [InlineData("Node already exists at path: Some/Path")]
    public void EitherWording_ClassifiesWithoutTheTypedReason(string message)
        => Assert.True(new InvalidOperationException(message).IsNodeAlreadyExists());

    [Fact]
    public void AnUnrelatedFailure_DoesNotClassify()
        => Assert.False(new InvalidOperationException("Node not found: Some/Path").IsNodeAlreadyExists());

    /// <summary>
    /// Every place in <c>src/</c> that reports "already exists" must produce a message this
    /// predicate matches. Without this, a producer rewording its message silently drops out of
    /// classification — which is the state the codebase was already in: three producers, two
    /// wordings, one string check covering two of them.
    /// </summary>
    [Fact]
    public void EveryProducersWording_IsMatchedByThePredicate()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        Assert.True(Directory.Exists(src), "src/ is gone — this guard now checks nothing.");

        // The literal as written at a producer: "Node already exists…{interpolation}"
        var literal = new Regex("\"(Node already exists[^\"]*)\"", RegexOptions.Compiled);
        var offenders = new List<string>();
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(path);
            foreach (Match m in literal.Matches(code))
            {
                found++;
                // Render the interpolation to a plausible path, then classify as a caller would.
                var rendered = m.Groups[1].Value.Replace("{node.Path}", "Some/Path").Replace("{path}", "Some/Path");
                if (!new InvalidOperationException(rendered).IsNodeAlreadyExists())
                    offenders.Add($"{Path.GetRelativePath(root, path).Replace('\\', '/')} — \"{rendered}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "A producer reports \"already exists\" in a wording NodeCreationFailure.IsNodeAlreadyExists "
            + "does not match, so callers classifying that failure will read it as a generic error — "
            + "which is #3407 (a release re-cut colliding with its own first attempt was swallowed "
            + "into null, and the NodeType went on advertising a build no release named)."
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o)));

        Assert.True(found >= 3,
            $"Expected at least 3 \"already exists\" literals under src/, saw {found}. The matcher "
            + "has gone blind, so this guard is reporting a clean tree having checked nothing.");
    }

    /// <summary>
    /// The already-exists mapping lives in exactly ONE place. Both single-node create surfaces used
    /// to carry their own copy, which is how the typed reason came to be dropped twice over.
    ///
    /// <para>Scoped to <c>NodeAlreadyExists =&gt;</c> deliberately, and not to "any
    /// <c>RejectionReason switch</c>": that pattern is general — delete, upsert, copy and bulk-create
    /// each map their own enum, and five such sites legitimately exist. Only the already-exists arm
    /// is the one this fix consolidated.</para>
    /// </summary>
    [Fact]
    public void NoHandRolledRejectionMapping_OutsideTheOnePlace()
    {
        var root = FindRepoRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f)
                .Contains("NodeCreationRejectionReason.NodeAlreadyExists =>", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => rel != "src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A create surface maps NodeCreationRejectionReason to an exception itself instead of "
            + "calling NodeCreationFailure.ToException. Its own copy will not stamp the typed reason "
            + "on Exception.Data, so callers fall back to parsing English — which is how #3407's "
            + "release re-cut collision came to be swallowed."
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root (MeshWeaver.slnx).");
        return dir!.FullName;
    }
}

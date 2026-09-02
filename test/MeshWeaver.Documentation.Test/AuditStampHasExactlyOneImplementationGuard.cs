#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The audit stamp — <c>LastModified</c> and <c>LastModifiedBy</c> on a
/// <c>stream.Update</c> write — must exist in exactly ONE place.</b>
///
/// <para><b>The measured cost of two copies (MeshWeaver#3021).</b> It was inlined twice. The
/// own-stream path stamped both fields; the sync-stream write-through path stamped only
/// <c>LastModifiedBy</c>. A write through the second therefore minted a NEW version while carrying
/// the snapshot's OWN <c>LastModified</c> — so a node's version order and its timestamp order
/// disagreed.</para>
///
/// <para>On a live install that presented as silent data loss: a Markdown node's newest version
/// carried a timestamp identical, to the second, to a much earlier version's, with a 45-minute
/// editing session's content gone. Nothing errored. It was noticed only because someone remembered
/// the content had been there before lunch.</para>
///
/// <para><b>Why a source guard rather than a behavioural test.</b> The divergent path is the
/// CROSS-HUB one, which needs a multi-hub fixture to exercise; the two paths were identical in
/// every observable respect except this stamp, so a single-process test passes against both the
/// bug and the fix. What actually failed here was not a behaviour nobody checked — it was two
/// copies of one rule drifting apart. This guard pins the property that makes the drift
/// impossible, which is the defect's real shape. A cross-hub behavioural test is still worth
/// adding and would catch a different failure: the helper being correct but not called.</para>
/// </summary>
public class AuditStampHasExactlyOneImplementationGuard
{
    private const string Subject = "src/MeshWeaver.Mesh.Contract/MeshNodeStreamExtensions.cs";

    /// <summary>
    /// The decision "the lambda did not set LastModified, so stamp it". Written as a regex over
    /// whitespace because the formatting, not the rule, is what varies.
    /// </summary>
    private static readonly Regex StampDecision =
        new(@"updated\s*\.\s*LastModified\s*==\s*current\s*\.\s*LastModified", RegexOptions.Compiled);

    private static string ReadSubject()
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), Subject);
        Assert.True(File.Exists(path), $"{Subject} is missing — this guard's subject moved; follow it.");
        return File.ReadAllText(path);
    }

    /// <summary>Executable lines only: the rule is quoted in prose here, deliberately.</summary>
    private static string ExecutableText(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                                                      && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void TheStampDecisionAppearsExactlyOnce()
    {
        var code = ExecutableText(ReadSubject());
        var hits = StampDecision.Matches(code).Count;

        // Control arm: zero means the matcher stopped recognising its subject, which would let
        // this guard pass having checked nothing — the failure mode it exists to prevent.
        Assert.True(hits > 0,
            "The audit-stamp decision (`updated.LastModified == current.LastModified`) was not found "
            + $"in {Subject}. Either the stamp moved — follow it — or this guard is now checking "
            + "nothing while reporting green.");

        Assert.True(hits == 1,
            $"The audit-stamp decision appears {hits} times in {Subject}; it must appear exactly ONCE, "
            + "inside ApplyAuditStamp.\n"
            + "Two copies is not a style problem: they drifted, and the copy that stamped only "
            + "LastModifiedBy let a cross-hub write mint a new version while carrying the snapshot's "
            + "own LastModified. A node's version order and timestamp order then disagree, which on a "
            + "live install destroyed a 45-minute editing session silently (#3021).\n"
            + "Call ApplyAuditStamp instead of inlining the rule again.");
    }

    [Fact]
    public void BothUpdatePathsCallTheSharedStamp()
    {
        var code = ExecutableText(ReadSubject());
        var calls = Regex.Matches(code, @"ApplyAuditStamp\s*\(").Count;

        Assert.True(calls >= 3,
            $"Expected the ApplyAuditStamp declaration plus at least two call sites in {Subject}; "
            + $"found {calls} occurrence(s). Both stream.Update paths — the own-stream write and the "
            + "cross-hub sync-stream write-through — must stamp through it. A path that stops calling "
            + "it silently reverts to writing whatever LastModified the caller's snapshot happened to "
            + "carry (#3021).");
    }
}

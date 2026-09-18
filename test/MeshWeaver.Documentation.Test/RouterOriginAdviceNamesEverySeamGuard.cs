#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 The <c>ROUTER_TRAFFIC ORIGIN</c> line must name EVERY seam a violating caller may hop onto —
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/4697">#4697</see>.
///
/// <para><b>What went wrong.</b> #4614 added a THIRD seam, <c>StreamSubscribingHub()</c>, because a
/// stream SUBSCRIPTION cannot use either of the other two: <c>portal/reads-{meshId}</c> registers no
/// handlers by design, so a subscription hopped onto it carries no <c>RouteStreamMessage</c> route
/// and hosts no <c>sync/{streamId}</c> sub-hub — the owner's fan-out arrives nowhere. The three
/// matchers in <see cref="RouterOriginScan"/> learned the new seam. The runtime line that TELLS a
/// caller which seam to use did not, and nothing compared the two: it went on naming
/// <c>NodeOperationIssuingHub()</c> and <c>ReadIssuingHub()</c> only.</para>
///
/// <para><b>Why that is worse than stale text.</b> The line is read by people AND by the incident
/// bot. #4697 was auto-filed off it and its "probable cause" paragraph repeated the printed
/// two-seam advice verbatim — so the wrong remedy was manufactured into a production issue, aimed at
/// an innocent call site. A reader who had followed it would have hopped a subscription onto
/// <c>ReadIssuingHub()</c> and lost the data together with the reports: the instrument goes quiet at
/// the same moment as the subject, which is the one failure mode a detector must never have.</para>
///
/// <para><b>The denominator is <see cref="RouterOriginScan.SeamNames"/> — the ONE place the seam
/// vocabulary is written</b>, since this change. Both <c>src/</c> ratchets build their matchers from
/// it, so a seam that is registered for them cannot be missing from the advice: this guard reds
/// until the line names it. A seam nobody registers there is invisible to all three, which is stated
/// rather than implied — the single list buys coupling, not omniscience.</para>
/// </summary>
public class RouterOriginAdviceNamesEverySeamGuard(ITestOutputHelper output)
{
    private const string MessageHubFile = "src/MeshWeaver.Messaging.Hub/MessageHub.cs";
    private const string MeshExtensionsFile = "src/MeshWeaver.Mesh.Contract/MeshExtensions.cs";

    /// <summary>The literal the ORIGIN report opens with, quote included so an XML doc that merely
    /// discusses the detector cannot be mistaken for the report itself.</summary>
    private const string OriginAnchor = "\"ROUTER_TRAFFIC ORIGIN:";

    [Fact]
    public void TheOriginLine_NamesEverySeamACallerMayHopOnto()
    {
        var root = SourceScan.FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, MessageHubFile));

        // NEGATIVE CONTROL. A guard that cannot find its subject passes having checked nothing —
        // the exact shape AGENTS.md calls a verification step that cannot fail. If the report is
        // renamed, moved or split, this fails LOUDLY and names the file rather than going green.
        var occurrences = Regex.Matches(source, Regex.Escape(OriginAnchor)).Count;
        occurrences.Should().Be(1,
            $"the ROUTER_TRAFFIC ORIGIN report is one log call in {MessageHubFile}, and this guard "
            + "reads the advice out of it — zero means it moved (and this guard would otherwise be "
            + "vacuous), more than one means the advice is now in two places that can disagree");

        var call = EnclosingCall(source, source.IndexOf(OriginAnchor, StringComparison.Ordinal));
        output.WriteLine($"ORIGIN report, {call.Length} chars, checked against "
            + $"{RouterOriginScan.SeamNames.Length} registered seams: "
            + string.Join(", ", RouterOriginScan.SeamNames));

        var missing = RouterOriginScan.SeamNames
            .Where(seam => !call.Contains(seam, StringComparison.Ordinal))
            .ToList();

        missing.Should().BeEmpty(
            "the ORIGIN line is the instrument a violating caller — and the incident bot — reads the "
            + "remedy off, so every seam the ratchets recognise has to be offered there. Missing: "
            + string.Join(", ", missing)
            + ". Naming a subset is not a partial answer but a WRONG one: #4614's subscription "
            + "family had to use StreamSubscribingHub(), and a caller who took one of the two names "
            + "the line printed instead would have hopped onto a hub with no handlers, stopping the "
            + "data along with the reports (#4697)");
    }

    /// <summary>
    /// The premise: the names this file ratchets on are real seams, not strings. Each must resolve
    /// to a <c>public static IMessageHub Name(this IMessageHub …)</c> in <c>MeshExtensions</c> — the
    /// NON-nullable return is what separates a seam (the identity function for a non-router hub)
    /// from the hub FACTORIES beside it (<c>MeshReadHub</c>, <c>MeshStreamHub</c>,
    /// <c>NodeOperationExecutionHub</c>), which answer <c>IMessageHub?</c> and are never what a
    /// caller should be told to post from. Without this, renaming a seam in <c>src/</c> would leave
    /// the ratchets and the log line agreeing with each other about a method that no longer exists.
    /// </summary>
    [Fact]
    public void EverySeamNameTheRatchetsUse_IsARealSeamOnMeshExtensions()
    {
        var root = SourceScan.FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(root, MeshExtensionsFile));

        RouterOriginScan.SeamNames.Length.Should().BeGreaterThanOrEqualTo(3,
            "the seam vocabulary is the denominator of this guard and of both src/ ratchets; an "
            + "empty or truncated list would make all three pass having required nothing");

        var unresolved = new List<string>();
        foreach (var seam in RouterOriginScan.SeamNames)
        {
            var declaration = new Regex(
                @"public\s+static\s+IMessageHub\s+" + Regex.Escape(seam) + @"\s*\(\s*this\s+IMessageHub\b");
            if (!declaration.IsMatch(source))
                unresolved.Add(seam);
        }

        unresolved.Should().BeEmpty(
            $"every name in RouterOriginScan.SeamNames must be a seam declared in {MeshExtensionsFile} "
            + "— `public static IMessageHub <name>(this IMessageHub …)`, the non-nullable shape that "
            + "returns the caller's own hub unchanged unless it is the router. Unresolved: "
            + string.Join(", ", unresolved));
    }

    /// <summary>
    /// The full call expression starting at the <c>LogError(</c> that encloses <paramref name="index"/>,
    /// read by balancing parentheses with string literals skipped — the advice itself contains both
    /// <c>(</c> and <c>)</c> (in <c>"(sender: "</c> and in every <c>Seam()</c> it names), so a
    /// balancer that did not skip literals would close the call in the middle of the message and
    /// this guard would silently check a prefix.
    /// </summary>
    private static string EnclosingCall(string source, int index)
    {
        var open = source.LastIndexOf('(', index);
        if (open < 0)
            throw new InvalidOperationException(
                $"no opening parenthesis before the ROUTER_TRAFFIC ORIGIN literal in {MessageHubFile}");

        var depth = 0;
        var inString = false;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '(': depth++; break;
                case ')':
                    if (--depth == 0)
                        return source[open..(i + 1)];
                    break;
            }
        }

        throw new InvalidOperationException(
            $"the ROUTER_TRAFFIC ORIGIN log call in {MessageHubFile} does not close — the guard "
            + "cannot read the advice out of it, and passing here would check nothing");
    }
}

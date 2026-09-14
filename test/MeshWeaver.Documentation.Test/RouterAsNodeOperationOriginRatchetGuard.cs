using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the router-as-node-CRUD-origin defect class (#1140): PRODUCTION code may
/// not issue a node-lifecycle request from a hub that can be the root mesh hub without hopping onto
/// <c>MeshExtensions.NodeOperationIssuingHub()</c>.
///
/// <para><b>Why this guard exists at all.</b> The same finding was filed four times — #1113, #1121,
/// #1136, #1140 — and ran to 41,087 <c>[Error]</c> lines on <c>memex</c> between 2026-08-10 and
/// 2026-09-12 before the two live call sites were found. The seams that fix it
/// (<c>NodeOperationIssuingHub</c> for CRUD, <c>ReadIssuingHub</c> for a one-shot read) are
/// <b>opt-in</b>, and until this guard the only <c>src/</c>-side enforcement was a runtime detector
/// that fires in PRODUCTION. <c>RouterRequestOriginSites.allow</c> covers the TEST tree only;
/// Doc/Architecture/RouterTrafficDetection said in as many words that "a new mesh-singleton that
/// posts node CRUD from its injected hub is a new #1140", i.e. that the class recurs by
/// construction. This is the missing half.</para>
///
/// <para><b>Why adopting the seam is never a behaviour change.</b>
/// <c>NodeOperationIssuingHub</c> returns the hub UNCHANGED unless its address type is the mesh
/// type — it is the identity function for every portal, session, layout, import and per-node hub.
/// So a site that is already off the router is byte-for-byte unaffected, and a site that is not is
/// corrected. That is what makes "adopt it everywhere" a rule rather than a judgement call, and it
/// is why this ratchet can demand the seam without asking each author to prove which hub reaches
/// their code.</para>
///
/// <para><b>What is matched.</b> A <c>.Observe(…)</c> / <c>.Post(…)</c> call whose FIRST argument is
/// a node-lifecycle request — either constructed inline (<c>new CreateNodeRequest(node)</c>) or
/// hoisted into a local earlier in the file (<c>var request = new DeleteNodeRequest(path); …
/// IssuingHub.Observe(request, …)</c>, which is <c>MeshService</c>'s own shape and which a
/// constructor-anchored scan would have classified on the wrong statement). The receiver is then
/// read as a primary expression and compared against the seam.</para>
///
/// <para><b>Deliberately NOT matched, and why each is a separate job.</b>
/// <list type="bullet">
///   <item>The READ seam (<c>ReadIssuingHub</c>). A one-shot node read has no request type of its
///     own to key on — it goes through <c>GetMeshNode</c>-shaped helpers — so the same marker
///     cannot see it. It is recognised as a seam here (a site that uses it is compliant) but its
///     absence is not reported. #2901 is why the two seams are separate in the first place: a read
///     with an HTTP request waiting on it must not queue behind node CRUD.</item>
///   <item>A request handed to a HELPER rather than posted (<c>EnsurePartitionBootstrap(hub, n,
///     new CreateNodeRequest(n))</c>). The post is in the helper, where this scan sees it with the
///     helper's own receiver; counting the construction too would double-count one site.</item>
///   <item>Message types other than node CRUD. #1140's evidence also names <c>ClickedEvent</c>,
///     <c>UnsubscribeRequest</c> and <c>PatchDataChangeRequest</c>; those have no single seam to
///     hop onto, and a marker that fired on the router's own routing duties is the one that gets
///     muted — the argument <c>RouterTrafficRule</c> itself makes for keying on the delivery's
///     ends rather than the handling hub.</item>
/// </list></para>
///
/// <para><b>The tolerances were measured, not guessed.</b> Over <c>src/</c> at the time of writing
/// this finds 23 node-CRUD post sites. A scan that required the request to be constructed INSIDE
/// the call's argument list finds 18 of them — the five it misses are every verb of
/// <c>MeshService</c>, the reference implementation. A seam test that accepted only the literal
/// <c>NodeOperationIssuingHub()</c> text misclassifies six more, because the idiomatic spelling
/// hoists it once per operation (<c>var issuingHub = hub.NodeOperationIssuingHub();</c>,
/// <c>private IMessageHub IssuingHub =&gt; _issuingHub ??= hub.NodeOperationIssuingHub();</c>).
/// Hence the alias pass. Its known approximation: an identifier is treated as a seam alias for the
/// WHOLE file once any assignment in that file derives it from a seam call, so a file that also
/// bound the same name to a bare hub elsewhere would be blessed. No file in the tree does, and the
/// alternative — resolving scopes — is a parser.</para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A stale line (its site was migrated) is reported, not failed — a gate that reds
/// <c>main</c> on the direction it is asking for teaches people to stop shrinking. Delete the stale
/// line and lower <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class RouterAsNodeOperationOriginRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new site in a file that already carries
    /// the shape; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 1;

    /// <summary>
    /// <c>src/</c> only. The test tree has its own ratchet with its own fix
    /// (<c>RouterAsTestRequestOriginRatchetGuard</c> → <c>MonolithMeshTestBase.RequestHub</c>);
    /// pointing one guard at both would offer the wrong remedy to half its findings.
    /// </summary>
    private static readonly string[] ScannedRoots = ["src"];

    /// <summary>
    /// The node-lifecycle verbs. These are the ones with a shared off-router execution hub behind
    /// them (<c>WithNodeOperationExecution</c>), which is what makes one seam the answer for all of
    /// them.
    /// </summary>
    private static readonly string[] RequestTypes =
    [
        "CreateNodeRequest", "CreateNodesRequest", "CreateOrUpdateNodeRequest",
        "DeleteNodeRequest", "MoveNodeRequest", "CopyNodeRequest",
    ];

    private static readonly string RequestAlternation = string.Join("|", RequestTypes);

    /// <summary>
    /// The request/response and fire-and-forget entry points, tolerant of an explicit response type
    /// argument (<c>Observe&lt;CreateNodeResponse&gt;(</c>) and of the line break C# style puts
    /// between the receiver and the call.
    /// </summary>
    private static readonly Regex CallMarker =
        new(@"\.\s*(?:Observe|Post)\s*(?:<[^<>()]*>\s*)?\(", RegexOptions.Compiled);

    private static readonly Regex NewRequest =
        new(@"\bnew\s+(?:" + RequestAlternation + @")\b", RegexOptions.Compiled);

    /// <summary>A local or field bound to a freshly constructed node-lifecycle request.</summary>
    private static readonly Regex RequestLocal =
        new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+(?:" + RequestAlternation + @")\b",
            RegexOptions.Compiled);

    /// <summary>A call to either seam. Both count: a site that hops for reads is off the router.</summary>
    private static readonly Regex SeamCall =
        new(@"(?:NodeOperationIssuingHub|ReadIssuingHub)\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>A name bound to a seam call — <c>var issuingHub = hub.NodeOperationIssuingHub();</c>
    /// and the lazily-cached property spelling both land here.</summary>
    /// <remarks>
    /// The gap excludes parentheses as well as statement punctuation. Without that, a default
    /// parameter value binds the alias to the wrong name: <c>Upsert(…, bool allow = false) =&gt;
    /// AsSystem(hub, () =&gt; hub.NodeOperationIssuingHub()…)</c> reads as "<c>allow</c> is a seam".
    /// Harmless in today's tree and exactly the sort of accident that blesses a site later.
    /// </remarks>
    private static readonly Regex SeamAlias =
        new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:=>|\?\?=|=)\s*[^;{}()]*?"
            + @"(?:NodeOperationIssuingHub|ReadIssuingHub)\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex BareIdentifier =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private const string AllowFileName = "RouterNodeOperationOriginSites.allow";

    /// <summary>One matched call: where it is, and whether its receiver is a seam.</summary>
    private readonly record struct Site(string File, int Line, bool OffRouter, string Receiver);

    [Fact]
    public void NoNewProductionSiteIssuesNodeCrudFromTheRouter()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);

        var violations = Scan(root)
            .Where(s => !s.OffRouter)
            .GroupBy(s => s.File, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var (file, count) in violations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — issue the request from "
                    + "hub.NodeOperationIssuingHub() (or hub.ReadIssuingHub() for a one-shot read). "
                    + "It returns the hub unchanged unless that hub is the ROUTER, so this is a "
                    + "no-op wherever the router is not reached. Do NOT add a line to "
                    + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a router-issued node "
                    + "operation was ADDED to a file that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = violations.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "Production code that posts node CRUD from the root mesh hub puts the ROUTER on both "
            + "ends of the delivery — and, when the request is target-less, EXECUTES the write on "
            + "the routing action block. That is #1140, which was filed four times and ran to "
            + "41,087 error lines before anyone could name a call site. Hop onto "
            + "MeshExtensions.NodeOperationIssuingHub().\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, in two halves that fail independently.
    ///
    /// <para>The first half proves the CLASSIFIER can reach both verdicts, by running the real
    /// scanning functions over a planted snippet that contains one of each. Without it a seam test
    /// that accidentally matched everything would report an empty violation set and the ratchet
    /// above would pass having blessed the tree.</para>
    ///
    /// <para>The second half proves the scan reached the PRODUCTION tree and recognised the shape
    /// there. The two fail independently and only together mean anything — a planted-tree self-test
    /// cannot prove the scanner found <c>src/</c> (SourceScan's own remarks, #2844), and a
    /// production count cannot prove the classifier has a failing branch.</para>
    /// </summary>
    [Fact]
    public void TheScannerReachesBothVerdictsAndSeesTheProductionTree()
    {
        const string planted = """
            class Planted
            {
                IObservable<int> Violating(IMessageHub hub) =>
                    hub.Observe(new CreateNodeRequest(node), o => o.WithTarget(t)).Select(_ => 1);

                IObservable<int> Compliant(IMessageHub hub) =>
                    hub.NodeOperationIssuingHub()
                        .Observe(new DeleteNodeRequest(path), o => o.WithTarget(t)).Select(_ => 1);

                IObservable<int> ViaAlias(IMessageHub hub)
                {
                    var issuing = hub.NodeOperationIssuingHub();
                    var request = new MoveNodeRequest(from, to);
                    return issuing.Observe(request, o => o.WithTarget(t)).Select(_ => 1);
                }

                IObservable<int> NotARequest(IMessageHub hub) =>
                    hub.Observe(somethingElse, o => o.WithTarget(t)).Select(_ => 1);
            }
            """;

        var plantedSites = SitesIn("planted.cs", planted);

        Assert.Equal(3, plantedSites.Count);
        Assert.Single(plantedSites.Where(s => !s.OffRouter));
        Assert.Equal(2, plantedSites.Count(s => s.OffRouter));
        Assert.Equal("hub", Assert.Single(plantedSites.Where(s => !s.OffRouter)).Receiver);

        // Comment masking, both sides. The same line is a site when it is code and nothing when it
        // is prose — which is the difference between measuring the tree and measuring the remarks
        // that describe it. This repo's remarks quote these shapes verbatim.
        const string asCode = "var x = hub.Observe(new CreateNodeRequest(n), o => o.WithTarget(t));";
        Assert.Single(SitesIn("code.cs", asCode));
        Assert.Empty(SitesIn("prose.cs", "// " + asCode));
        Assert.Empty(SitesIn("literal.cs", "var s = \"" + asCode.Replace("\"", "") + "\";"));

        var found = Scan(SourceScan.FindRepoRoot());

        Assert.True(found.Count > 0,
            "The scanner found NO node-lifecycle post site anywhere under "
            + string.Join(", ", ScannedRoots)
            + ". src/ cannot be free of node CRUD, so this is a BROKEN SCAN reporting as a clean "
            + "tree — the failure mode SourceScan's remarks describe (#2844). Fix the scan; never "
            + "soften the guard that surfaced it.");
        Assert.True(found.Any(s => s.OffRouter),
            "The scanner classified EVERY production site as router-issued. MeshService alone hops "
            + "on all five of its verbs, so the seam test is broken and every count is unreliable.");
    }

    private static IReadOnlyList<Site> Scan(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .SelectMany(f => SitesIn(SourceScan.Relative(root, f), ReadOrEmpty(f)))
            .ToList();

    private static string ReadOrEmpty(string path)
    {
        // A file a concurrent build is writing is not evidence.
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; }
    }

    /// <summary>
    /// Every <c>.Observe</c>/<c>.Post</c> call in <paramref name="text"/> whose first argument is a
    /// node-lifecycle request, classified by whether its receiver is one of the off-router seams.
    /// Comments and string literals are masked first, so remarks quoting the shape are not counted.
    /// </summary>
    private static IReadOnlyList<Site> SitesIn(string file, string text)
    {
        if (!RequestTypes.Any(t => text.Contains(t, StringComparison.Ordinal)))
            return [];

        var code = SourceScan.MaskCommentsAndStrings(text);

        var requestLocals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in RequestLocal.Matches(code))
            if (!requestLocals.ContainsKey(m.Groups[1].Value))
                requestLocals[m.Groups[1].Value] = m.Index;

        var seamAliases = SeamAlias.Matches(code)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var sites = new List<Site>();
        foreach (Match call in CallMarker.Matches(code))
        {
            var openParen = call.Index + call.Length - 1;
            var argument = SourceScan.FirstArgument(code, openParen).Trim();

            if (!NewRequest.IsMatch(argument))
            {
                // The hoisted spelling: the request was built into a local a few lines up.
                var head = argument.Split('.')[0].Trim();
                if (!BareIdentifier.IsMatch(head)
                    || !requestLocals.TryGetValue(head, out var boundAt)
                    || boundAt >= call.Index)
                    continue;
            }

            var receiver = Receiver(code, call.Index);
            var offRouter = SeamCall.IsMatch(receiver) || IsSeamAlias(receiver, seamAliases);
            sites.Add(new Site(file, LineOf(code, call.Index), offRouter, receiver));
        }

        return sites;
    }

    private static int LineOf(string code, int index) =>
        code.AsSpan(0, index).Count('\n') + 1;

    /// <summary>
    /// The primary expression immediately left of the <c>.</c> at <paramref name="dot"/> — an
    /// identifier, or a dotted chain whose links may carry argument lists
    /// (<c>hub.NodeOperationIssuingHub()</c>). Whitespace is collapsed so a receiver wrapped across
    /// lines compares equal to one that is not.
    ///
    /// <para>Walking the expression rather than taking the preceding text is what keeps the verdict
    /// honest: a window back to the previous <c>;</c> picks up the tail of whatever lambda came
    /// before, which on the copy helper's ternary read as a receiver of
    /// <c>"…NodeCopyDisposition.Updated); }) : hub"</c>.</para>
    /// </summary>
    private static string Receiver(string code, int dot)
    {
        var i = dot;
        while (true)
        {
            i = SkipWhitespaceBack(code, i);
            if (i > 0 && code[i - 1] is ')' or ']')
            {
                i = SkipGroupBack(code, i);
                i = SkipWhitespaceBack(code, i);
            }

            if (i > 0 && IsIdentifierChar(code[i - 1]))
            {
                while (i > 0 && IsIdentifierChar(code[i - 1])) i--;
            }
            else
            {
                break;
            }

            var beforeName = SkipWhitespaceBack(code, i);
            // A single '.' continues the chain; '..' is a range and is not part of one.
            if (beforeName > 0 && code[beforeName - 1] == '.'
                               && !(beforeName > 1 && code[beforeName - 2] == '.'))
            {
                i = beforeName - 1;
                continue;
            }

            break;
        }

        return string.Join(' ', code[i..dot].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Whether <paramref name="receiver"/> names a hub bound to a seam call — the bare identifier
    /// (<c>issuingHub</c>) or the last link of a qualified one (<c>this.issuingHub</c>). Qualified
    /// links carrying an argument list are left to <see cref="SeamCall"/>.
    /// </summary>
    private static bool IsSeamAlias(string receiver, IReadOnlySet<string> aliases)
    {
        if (aliases.Contains(receiver)) return true;
        var lastDot = receiver.LastIndexOf('.');
        if (lastDot < 0) return false;
        var tail = receiver[(lastDot + 1)..];
        return !tail.Contains('(') && aliases.Contains(tail);
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipWhitespaceBack(string code, int i)
    {
        while (i > 0 && char.IsWhiteSpace(code[i - 1])) i--;
        return i;
    }

    /// <summary>Index of the opener matching the closer at <c>code[i - 1]</c>.</summary>
    private static int SkipGroupBack(string code, int i)
    {
        var close = code[i - 1];
        var open = close == ')' ? '(' : '[';
        var depth = 0;
        for (var j = i; j > 0; j--)
        {
            if (code[j - 1] == close) depth++;
            else if (code[j - 1] == open && --depth == 0) return j - 1;
        }

        return 0;
    }
}

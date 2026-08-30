using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the router-as-request-origin defect class (#2423): a test may not issue a
/// request/response exchange FROM the root mesh hub at a new site.
///
/// <para><b>Why this is a defect and not a style preference.</b> <c>MonolithMeshTestBase.Mesh</c> (and
/// <c>HubTestBase.Mesh</c>) resolve the ROOT <c>mesh/{id}</c> hub — the mesh's ROUTER.
/// <c>Mesh.Observe(request, o =&gt; o.WithTarget(new Address(nodePath)))</c> therefore makes the router
/// an END of the delivery in BOTH directions: the request leaves stamped <c>Sender = mesh/{id}</c> and
/// the response is addressed straight back at <c>mesh/{id}</c>. <c>RouterTrafficRule.RoleOf</c> reports
/// both, so every affected test class logs two <c>[Error]</c> lines — and a channel that errors on
/// ordinary test traffic is a channel people learn to skip, which is how a real router-starvation
/// report (prod 2026-06-11: node-CRUD bursts starving <c>SubscribeRequest</c>) goes unread. It is also
/// the wrong SHAPE: no production caller drives the mesh from the router — requests come from an MCP
/// session hub, the Blazor portal hub, a layout-area hub, a per-node hub, all of which the router
/// merely FORWARDS (a hop, which the detector deliberately does not report).</para>
///
/// <para><b>The fix at a site</b> is <c>MonolithMeshTestBase.RequestHub</c> — one lazily-created client
/// hub per test method — for anything addressing a NODE, and
/// <c>MonolithMeshTestBase.ObserveNodeOperation</c> for node CRUD, which additionally aims the
/// request at <c>NodeOperationTarget()</c>: the production seam <c>MeshService</c> uses and
/// <c>NodeOperationOriginTest</c> pins.</para>
///
/// <para><b>Why the file is seeded rather than empty.</b> Four files are LEGITIMATE and are expected
/// to stay: in each, the router is the SUBJECT of the test rather than its transport.
/// <c>RouterTrafficDetectorTest</c> asserts the detector still FIRES for a mesh-hub sender — it must
/// produce the shape. <c>StreamRouteSelfParentTerminationTest</c> asserts the mesh hub's own drain
/// stays responsive after a poison StreamMessage, so the mesh hub has to be the target.
/// <c>UpsertInnerCreateObservationTest</c> pins the #981 leak, which is literally a self-targeted
/// <c>CreateNodeRequest@mesh/&lt;self&gt;</c> whose reply the router's own <c>HandleCallbacks</c>
/// drops. <c>ShutdownRoutingRejectClassificationTest</c> depends on an ORDERING on the router's
/// action block — stall, then probe, then <c>Dispose()</c> — that a routed hop from a client hub
/// would no longer guarantee. Each carries a 🚨 comment at the site saying so.</para>
///
/// <para><b>Deliberately NOT matched, and why each is a separate job.</b>
/// <list type="bullet">
///   <item><c>Mesh.Post(...)</c> — fire-and-forget from the router covers genuine routing duties (a
///     <c>HeartBeatEvent</c>, which <c>RouterTrafficRule</c> itself excludes; a
///     <c>DisposeRequest</c> to a hub the mesh hosts) alongside real violations (a target-less
///     <c>TrackActivityRequest</c>, which EXECUTES on the router's action block). One marker cannot
///     tell them apart, and a ratchet that fires on the router's actual job is the one that gets
///     muted — the same argument <c>ReportRouterTraffic</c> makes for keying on the delivery's ends
///     rather than the handling hub.</item>
///   <item><c>client.Observe(req, o =&gt; o.WithTarget(Mesh.Address))</c> — the TARGET half. The
///     origin is already off the router, but the request still executes ON it, so the detector
///     reports the <c>"target"</c> role. ~60 such sites remain (mostly node CRUD in
///     <c>RlsIntegrationTests</c> and <c>MeshWeaver.Threading.Test</c>); retargeting them at
///     <c>NodeOperationTarget()</c> moves execution onto a hub with a different permission surface,
///     which is a judgement call per security assertion rather than a rename.</item>
/// </list>
/// Both are tracked on #2423 for a follow-up.</para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file, a raised count, or a raised TOTAL is a
/// failure. A stale line (its site was migrated) is reported, not failed — a gate that reds
/// <c>main</c> on the direction it is asking for teaches people to stop shrinking. Delete the stale
/// line and lower <see cref="TotalBudget"/> in the same change.</para>
/// </summary>
public class RouterAsTestRequestOriginRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size. Per-file entries stop a new site in a file that already carries
    /// the shape; this stops the list as a WHOLE from growing — including by the trick of adding a
    /// new file's line. Lower it whenever you delete or lower an entry.
    /// </summary>
    private const int TotalBudget = 3;

    /// <summary>
    /// <c>test/</c> only. In <c>src/</c> the same shape is already handled by the shared
    /// <c>MeshExtensions.NodeOperationIssuingHub</c> seam, which hops a root-hub caller off the
    /// router; this guard exists because the test harness was treated as exempt.
    /// </summary>
    private static readonly string[] ScannedRoots = ["test"];

    /// <summary>
    /// Both spellings of the request/response entry point — the non-generic
    /// <c>Observe(request, options)</c> and the explicit <c>Observe&lt;TResponse&gt;(...)</c> — and
    /// tolerant of the line break C# style puts between the receiver and the call.
    ///
    /// <para>🚨 Both tolerances were LEARNED, not guessed. #2423's inventory grepped the contiguous
    /// <c>Mesh.Observe(</c>; over <c>test/</c> that finds 56 of the 85 sites this regex finds, so a
    /// substring marker missed <b>29</b> — <c>Mesh.Observe&lt;TResponse&gt;(</c> is a different
    /// string, and eighteen sites wrap as <c>await Mesh</c> ⏎ <c>.Observe&lt;…&gt;(</c>. Among the
    /// misses were eleven target-less node-CRUD requests that EXECUTED on the router. A marker that
    /// matches one spelling of a shape measures the spelling, not the shape.</para>
    ///
    /// <para>The leading look-behind is what keeps <c>siloMesh.Observe&lt;…&gt;</c> — a different,
    /// local hub in the Orleans harness — out of the count.</para>
    /// </summary>
    private static readonly Regex Marker =
        new(@"(?<![A-Za-z0-9_])Mesh\s*\.\s*Observe\s*[(<]", RegexOptions.Compiled);

    private const string AllowFileName = "RouterRequestOriginSites.allow";

    [Fact]
    public void NoNewTestIssuesARequestFromTheRootMeshHub()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var found = Scan(root);

        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — issue the request from MonolithMeshTestBase."
                    + "RequestHub (a client hub), and for node CRUD aim it at "
                    + "RequestHub.NodeOperationTarget(). Do NOT add a line to " + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a router-issued request was "
                    + "ADDED to a file that already carries the shape.");
        }

        var total = allowed.Values.Sum();
        if (total > TotalBudget)
            failures.Add(
                $"  TOTAL      {total} allowances > {TotalBudget} budgeted — the inventory GREW. "
                + "Adding a line to " + AllowFileName + " is not a fix.");

        foreach (var (file, budget) in allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")} and lower "
                    + $"TotalBudget by {budget - count}.");
        }

        Assert.True(failures.Count == 0,
            "A test that drives the mesh through the ROUTER puts the mesh hub on both ends of the "
            + "delivery — two ROUTER_TRAFFIC [Error] lines per class, and a shape no production "
            + "caller uses (#2423). Issue it from RequestHub instead.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scanner must actually SEE the shape. The seeded allow
    /// file is non-empty, so a scanner that silently matched nothing — a renamed marker, a masking bug
    /// that blanked every file — would report every entry as STALE rather than fail, and the ratchet
    /// above would pass on no evidence.
    /// </summary>
    [Fact]
    public void TheScannerFindsTheShapeItIsRatcheting()
    {
        var root = SourceScan.FindRepoRoot();
        var found = Scan(root);

        Assert.True(found.Count > 0,
            "The scanner found no router-issued request anywhere under " + string.Join(", ", ScannedRoots)
            + ". Either every site was migrated — in which case empty " + AllowFileName
            + " and delete this assertion — or the scanner is broken.");

        // Masking canary — THIS file. Its remarks quote the shape verbatim and the assertion below
        // carries it as a string literal, so every occurrence here is prose or data and none is a
        // call site. A scanner that counted them would be ratcheting against its own documentation,
        // and every number in the allow file would be wrong.
        var self = Path.Combine(root, "test", "MeshWeaver.Documentation.Test",
            "RouterAsTestRequestOriginRatchetGuard.cs");
        Assert.True(File.Exists(self), "the canary file must exist for this check to mean anything");
        Assert.Contains("Mesh.Observe(", File.ReadAllText(self), StringComparison.Ordinal);
        Assert.False(
            found.ContainsKey("test/MeshWeaver.Documentation.Test/RouterAsTestRequestOriginRatchetGuard.cs"),
            "the scanner counted a COMMENT or a string literal as a call site — comment/string "
            + "masking is broken, and every count in the allow file is therefore unreliable.");

        // `siloMesh.Observe<…>` (Orleans harness) must NOT be counted: it is a different local hub,
        // and a marker that swallowed any identifier ending in `Mesh` would inflate every count.
        Assert.False(found.ContainsKey("test/MeshWeaver.Hosting.Orleans.Test/OrleansPythonCodeNodeGateDeliveryTest.cs"),
            "the scanner matched an identifier merely ENDING in `Mesh` — every count is then unreliable.");
    }

    private static Dictionary<string, int> Scan(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (Relative: SourceScan.Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Occurrences of <see cref="Marker"/>, with comments and string literals masked first so the
    /// remarks that quote the shape are not counted as call sites.
    /// </summary>
    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        if (!text.Contains("Observe", StringComparison.Ordinal)) return 0;

        return Marker.Matches(SourceScan.MaskCommentsAndStrings(text)).Count;
    }
}

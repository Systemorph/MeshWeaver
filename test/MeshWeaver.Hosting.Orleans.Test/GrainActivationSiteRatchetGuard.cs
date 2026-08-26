using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Governance ratchet for the invariant <b>"no deactivating silo should activate any grain
/// whatsoever"</b>: <c>GetGrain</c> may not appear at a NEW site in <c>src/</c>.
///
/// <para><b>Why a call site is the unit.</b> Asking Orleans for a grain reference and calling it is
/// what CREATES an activation, so every such site is a place the rule can be broken. Orleans itself
/// refuses correctly once it knows — <c>Catalog.GetOrCreateActivation</c> creates only while
/// <c>_siloStatusOracle.CurrentStatus == SiloStatus.Active</c>, and
/// <c>PlacementService.GetCompatibleSilos</c> intersects with the ACTIVE silo set — but
/// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/> fires
/// strictly EARLIER than the membership oracle leaves <c>Active</c>. In that window Orleans will
/// faithfully create whatever we ask for, on the silo that is leaving. The mesh's job is therefore
/// not to refuse — it is to STOP ASKING, and that decision has to live at every site.</para>
///
/// <para><b>What this guard buys over a code review.</b> The defect it ratchets was introduced by a
/// call site added three lines away from the gate that would have caught it
/// (<c>OrleansRoutingService.AttachPodHub</c> reached <c>IGrainFactory</c> directly while
/// <c>DeliverMessage</c> right above it already short-circuited on the stopping token), and it is
/// invisible at the site: the code reads as an ordinary release of a claim. PR #2252 is the same
/// shape one level up — an announcement that escaped through a healthy ancestor re-activated a grain
/// that was mid-deactivation, and the straggler test then waited forever for an activation that kept
/// being re-created.</para>
///
/// <para><b>The two allowances are deliberate, and opposite.</b>
/// <c>OrleansRoutingService</c> holds exactly ONE — inside <c>GrainWhileRunning</c>, the seam that
/// applies the gate, which is why every other call in that file goes through it.
/// <c>RoutingGrain</c> holds TWO, and they are deliberately UNGATED because they are the DRAIN: a
/// message already accepted for routing must still land, and Orleans' own placement is what sends it
/// to a HEALTHY silo rather than this one. Gating there would drop live work instead of relocating it
/// — the invariant is "no new activations", never "no traffic" (#1971).</para>
///
/// <para><b>The ratchet may only SHRINK.</b> A new file or a raised count is a failure; a line that
/// has become stale is reported, not failed, so two PRs shrinking concurrently cannot red main.
/// If you genuinely need a new grain call, route it through a gated seam — do NOT add a line
/// here.</para>
/// </summary>
public class GrainActivationSiteRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory: relative path → how many <c>GetGrain</c> sites that file may carry, and
    /// WHY each one is there.
    ///
    /// <para>🚨 <b>The reason is not decoration — it is the cost of raising a number.</b> A bare count
    /// invites a reflexive bump the moment a legitimate change lands, and a ratchet that gets bumped
    /// without thought is a ratchet that has already died. To raise one of these you must name the
    /// call you added and state why the invariant does not apply to it, in the same edit. If you
    /// cannot write that sentence, the call belongs behind the seam instead.</para>
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new(StringComparer.Ordinal)
    {
        // The SEAM itself. Exactly one call, inside GrainWhileRunning, which is the gate — and
        // TheOnlyUngatedDoorInTheRouterIsTheSeam pins that it is still that call and not another.
        ["src/MeshWeaver.Connection.Orleans/OrleansRoutingService.cs"] =
            (1, "the seam: GrainWhileRunning, the one gated door to a grain in this process"),

        // The DRAIN, deliberately UNGATED — every one of these answers or delivers a message that has
        // ALREADY been accepted for routing, and Orleans' own placement is what sends it to a HEALTHY
        // silo rather than this one. Named individually so a fourth cannot ride in on the number:
        //   1. BuildPodHubRoute      → IPodHubGrain.Deliver        — forward leg to a pod-hosted hub.
        //   2. BuildGrainRoute       → IMessageHubGrain.DeliverMessage — forward leg to a node hub.
        //   3. PostFailure           → IPodHubGrain.Deliver        — the NACK leg (#1742 residual C).
        //      Added on main after this guard was seeded. It is drain, not courtesy: refusing it
        //      would park a waiting sender on a silence, which is the very defect that PR fixed.
        //      It already limits its own activation churn by asking only for stream-routed sender
        //      types, so it never mints an activation just to be told PodHubNotHere.
        ["src/MeshWeaver.Hosting.Orleans/RoutingGrain.cs"] =
            (3, "the drain: forward legs + the NACK leg, all answering already-accepted deliveries"),
    };

    private const string Marker = "GetGrain";

    /// <summary>Production roots only. A grain call in a test stands up its own cluster and cannot
    /// strand a production silo.</summary>
    private static readonly string[] ScannedRoots = ["src", "memex", "tools"];

    private static readonly string[] ExcludedSegments =
        ["bin", "obj", "node_modules", "TestResults", ".git", ".vs", "dist"];

    [Fact]
    public void NoNewSiteAsksOrleansForAGrain()
    {
        var root = FindRepoRoot();
        var found = Scan(root);
        var failures = new List<string>();

        foreach (var (file, count) in found.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!Allowed.TryGetValue(file, out var allowance))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — a new place that can create a grain activation. "
                    + "Route it through a gated seam (OrleansRoutingService.GrainWhileRunning) so a "
                    + "host that has begun stopping asks Orleans for nothing. Do NOT add a line to "
                    + "the Allowed table.");
            else if (count > allowance.Count)
                failures.Add(
                    $"  MORE       {file} ({count} > {allowance.Count} allowed — allowed because: "
                    + $"{allowance.Why}) — a site was ADDED to a file that already carries grain "
                    + "calls. If the new call is DRAIN (it delivers or answers a message already "
                    + "accepted for routing), name it in the Allowed table's comment and raise the "
                    + "count in the same edit. If it is anything else — a claim, a release, an "
                    + "announcement, a probe — it must go behind the seam instead.");
        }

        // Stale entries are reported, never failed — shrinking is the direction this guard exists to
        // encourage, and failing on it would red main whenever two PRs shrink concurrently.
        foreach (var (file, budget) in Allowed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var count = found.GetValueOrDefault(file, 0);
            if (count < budget.Count)
                output.WriteLine(
                    $"STALE (please tidy): {file} — {count} found, {budget.Count} allowed. "
                    + $"{(count == 0 ? "Delete the line" : $"Lower it to {count}")}.");
        }

        Assert.True(failures.Count == 0,
            "A silo that has begun shutting down must never create a NEW grain activation — not for a "
            + "routed message, not for a stream delivery, not for a \"goodbye\" announcement. Orleans "
            + "enforces that once its membership oracle leaves Active; ApplicationStopping fires "
            + "earlier, and in that window only WE can stop asking.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Non-vacuity, pinned in the same run: the scanner must actually SEE the sites it ratchets.
    /// A renamed marker or a masking bug that blanked every file would otherwise report every entry
    /// as STALE and pass on no evidence — the exact failure mode this repo has been bitten by.
    /// </summary>
    [Fact]
    public void TheScannerFindsTheSitesItIsRatcheting()
    {
        var root = FindRepoRoot();
        var found = Scan(root);

        Assert.True(found.Count > 0,
            "The scanner found no GetGrain call anywhere under " + string.Join(", ", ScannedRoots)
            + " — the mesh cannot route without one, so the scanner is broken and the ratchet above "
            + "passes on no evidence.");

        var seam = "src/MeshWeaver.Connection.Orleans/OrleansRoutingService.cs";
        Assert.True(found.ContainsKey(seam),
            $"The scanner did not see the seam's own call in {seam}. Every count is therefore "
            + "unreliable.");

        // The seam's remarks discuss GetGrain in prose. A scanner that counted comments would
        // ratchet against documentation, so prove the masking works: the seam has exactly one CALL.
        Assert.Equal(1, found[seam]);
    }

    /// <summary>
    /// The count alone is not the invariant. <c>OrleansRoutingService</c> may hold exactly one
    /// <c>GetGrain</c>, and this pins that the one it holds is still the GATED one — swapping the
    /// seam's call for an ungated call elsewhere in the file keeps the count at 1 and would sail
    /// through the ratchet above while reopening the whole defect.
    /// </summary>
    [Fact]
    public void TheOnlyUngatedDoorInTheRouterIsTheSeam()
    {
        var root = FindRepoRoot();
        var file = Path.Combine(root, "src", "MeshWeaver.Connection.Orleans", "OrleansRoutingService.cs");
        Assert.True(File.Exists(file), $"the router must exist for this check to mean anything: {file}");

        var code = MaskCommentsAndStrings(File.ReadAllText(file));
        var at = code.IndexOf("GetGrain", StringComparison.Ordinal);
        Assert.True(at > 0, "the router must still contain its one grain call");

        // The gate and the call belong to the same expression. Read back far enough to survive
        // reformatting, but not so far that a neighbouring method could satisfy it.
        var from = Math.Max(0, at - 300);
        var window = code[from..at];
        Assert.True(window.Contains("IsHostStopping", StringComparison.Ordinal),
            "OrleansRoutingService's GetGrain is no longer guarded by IsHostStopping. The seam "
            + "(GrainWhileRunning) is the ONLY place this class may reach a grain, because a host "
            + "that has begun stopping must ask Orleans for nothing — Orleans still reports Active "
            + "for some seconds after ApplicationStopping fires, and will faithfully create the "
            + "activation on the silo that is leaving.");
    }

    private static Dictionary<string, int> Scan(string root) =>
        ScannedRoots
            .Select(r => Path.Combine(root, r))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => Path.GetExtension(f) is ".cs" or ".razor")
            .Where(f => !IsExcluded(root, f))
            .Select(f => (Relative: Relative(root, f), Count: CountSites(f)))
            .Where(x => x.Count > 0)
            .ToDictionary(x => x.Relative, x => x.Count, StringComparer.Ordinal);

    /// <summary>
    /// Occurrences of <see cref="Marker"/> that are CALLS — comments and string literals are masked
    /// first, and the marker must be followed by a call's opening parenthesis (optionally through a
    /// generic argument list), so <c>GetGrainCount</c> or a prose mention is not a site.
    /// </summary>
    private static int CountSites(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return 0; } // a file a concurrent build is writing is not evidence

        if (!text.Contains(Marker, StringComparison.Ordinal)) return 0;

        var code = MaskCommentsAndStrings(text);
        var count = 0;
        var at = 0;
        while ((at = code.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
        {
            var start = at;
            at += Marker.Length;

            // `xGetGrain` / `GetGrainCount` are different identifiers, not this one.
            if (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) continue;

            var i = at;
            // Skip a generic argument list: GetGrain<IPodHubGrain>(…)
            if (i < code.Length && code[i] == '<')
            {
                var depth = 0;
                while (i < code.Length)
                {
                    if (code[i] == '<') depth++;
                    else if (code[i] == '>' && --depth == 0) { i++; break; }
                    i++;
                }
            }

            while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
            if (i < code.Length && code[i] == '(') count++;
        }

        return count;
    }

    /// <summary>Blanks comments and string/char literals so prose and quoted code are not counted.</summary>
    private static string MaskCommentsAndStrings(string text)
    {
        var sb = new StringBuilder(text);
        var i = 0;
        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') sb[i++] = ' ';
            }
            else if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                while (i < text.Length && !(i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/'))
                    sb[i++] = ' ';
                if (i + 1 < text.Length) { sb[i++] = ' '; sb[i++] = ' '; }
            }
            else if (text[i] == '"' || text[i] == '\'')
            {
                var quote = text[i];
                sb[i++] = ' ';
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) sb[i++] = ' ';
                    if (i < text.Length) sb[i++] = ' ';
                }
                if (i < text.Length) sb[i++] = ' ';
            }
            else i++;
        }

        return sb.ToString();
    }

    private static bool IsExcluded(string root, string file) =>
        Relative(root, file).Split('/').Any(seg => ExcludedSegments.Contains(seg, StringComparer.OrdinalIgnoreCase));

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

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

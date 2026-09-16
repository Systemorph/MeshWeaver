using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the second half of
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see>: once a file has
/// DECLARED a hub reference router-capable — by calling <c>NodeOperationIssuingHub()</c> or
/// <c>ReadIssuingHub()</c> on it — no OTHER targeted <c>.Post</c>/<c>.Observe</c> in that file may
/// still be issued from the bare reference.
///
/// <para><b>Why a second ratchet rather than a wider first one.</b>
/// <see cref="RouterAsNodeOperationOriginRatchetGuard"/> keys on the MESSAGE: a post is a site when
/// the framework registers a lifecycle handler for what it carries. That denominator is derived and
/// it is right for what it covers, but it is a denominator over messages, and the seam is a property
/// of the HUB. So it can only ever see the lifecycle slice of any one receiver — and #1140's
/// evidence is mostly NOT lifecycle: its type list is
/// <c>RawJson</c>/<c>UnsubscribeRequest</c>/<c>ValidateTokenRequest</c>/<c>PatchDataChangeRequest</c>,
/// none of which the framework registers a lifecycle handler for.</para>
///
/// <para>🚨 <b>The measured consequence, on today's tree.</b>
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/4463">#4463</see> named
/// <c>MeshWeaver.AI.MeshOperations</c> in production —
/// <c>ROUTER_TRAFFIC ORIGIN: DisposeRequest was POSTED with the mesh hub as sender … at
/// MeshWeaver.AI.MeshOperations+&lt;&gt;c__DisplayClass95_0.&lt;RecycleCore&gt;b__3</c>, 2026-09-16
/// 01:10:13Z — which is a PROOF, not an inference, that that class's <c>hub</c> field is the router.
/// #4477 hopped that one line, because <c>DisposeRequest</c> was the one message on that field the
/// message-keyed ratchet could see. FIVE live sibling exchanges on the identical field — three
/// content-collection reads, the UCR read and the script dispatch — kept leaving stamped
/// <c>Sender = mesh/{id}</c> with their replies addressed straight back at <c>mesh/{id}</c>, and
/// that pair IS #1140's receiver-side line
/// (<c>RawJson has the mesh hub as sender … target: &lt;node path&gt;</c>). Two more of the same
/// shape sat in private methods the repo called from nowhere; they were DELETED rather than hopped,
/// because a router-issuing shape in dead code is a loaded gun for whoever wires it up next rather
/// than a site to correct. The same happened one class over: <c>MeshNodeEditor.Move</c> hopped (a
/// lifecycle verb) while the <c>DataChangeRequest</c> in <c>MeshNodeEditor.Update</c>, ten lines
/// above it on the same field, did not.</para>
///
/// <para><b>THE DENOMINATOR IS DERIVED, from production, and it is not a list of anything.</b> A
/// receiver is router-capable when the code ITSELF says so: <c>X.NodeOperationIssuingHub()</c> or
/// <c>X.ReadIssuingHub()</c> appearing anywhere in a file is that file's own statement that
/// <c>X</c> can be the root mesh hub — the seams are the identity function for every other hub, so
/// nobody writes one about a reference that cannot be the router. You cannot adopt the seam for one
/// message on a hub without making that statement, which is why this cannot be forgotten the way a
/// marker attribute or a name in a test can. It is also SELF-EXTENDING: the moment a new file's
/// first site is hopped, every sibling post on that same reference joins this denominator.</para>
///
/// <para><b>What it cannot see, stated rather than implied.</b> A file that has NEVER hopped
/// anything declares nothing, so its posts are invisible here — exactly as a non-lifecycle message
/// is invisible to the sibling guard. The two ratchets are complements, not a cover: between them
/// they see every lifecycle message anywhere, plus every message on a receiver already known to
/// reach the router. What remains uncovered is a brand-new mesh-singleton posting non-lifecycle
/// work, and for that the instrument is still the runtime <c>ROUTER_TRAFFIC ORIGIN</c> line — which
/// since #4463 names the call site, so it is a five-minute question rather than a month-long
/// one.</para>
///
/// <para>🚨 <b>One structural exclusion, the same one and for the same reason.</b> A post that
/// names no target, or names only the RECEIVER's own address, is out: the seam's whole effect is to
/// issue the delivery from a DIFFERENT hub, which for a self-directed message sends it somewhere
/// else entirely. A rule whose remedy is nonsense at a site must not report that site. Today that
/// covers exactly one site — the #981 self-targeted inner create inside the
/// <c>CreateOrUpdateNodeRequest</c> handler, which <c>RouterNodeOperationOriginSites.allow</c>
/// already documents as not-debt.</para>
///
/// <para><b>Adopting the seam is never a behaviour change</b>, which is what lets this be a rule
/// rather than a judgement call: both seams return the hub unchanged unless its address type is the
/// mesh type. A site already off the router is byte-for-byte unaffected; a site that is not is
/// corrected. A false positive therefore costs nothing but a hop that does nothing.</para>
///
/// <para>The matcher is <see cref="RouterOriginScan"/> — SHARED with the sibling guard, so the
/// syntax-only evasions closed on #4477's review round are closed here by construction rather than
/// by a second attempt at the same regexes.</para>
/// </summary>
public class RouterAsRouterCapableReceiverRatchetGuard(ITestOutputHelper output)
{
    /// <summary>
    /// The seeded inventory's size — ZERO. Every site this guard's first run found was FIXED rather
    /// than listed; the one shape it must not report is excluded structurally (above), not allowed.
    /// If you are about to raise this, you are about to write down a router-issued delivery as
    /// acceptable: read <see cref="RouterAsNodeOperationOriginRatchetGuard"/>'s allow-file header
    /// first, which explains what the one allowance over there actually is.
    /// </summary>
    private const int TotalBudget = 0;

    /// <summary>
    /// <c>src/</c> only. The test tree has its own ratchet with its own fix
    /// (<c>RouterAsTestRequestOriginRatchetGuard</c> → <c>MonolithMeshTestBase.RequestHub</c>).
    /// </summary>
    private static readonly string[] ScannedRoots = ["src"];

    private const string AllowFileName = "RouterCapableReceiverOriginSites.allow";

    /// <summary>
    /// Anchors the derivation on real production files. Reading these out of a constant is the
    /// opposite of restating the denominator: the NAMES here are not the rule, they are proof that
    /// the rule reached the tree. A rename reddens this guard instead of silently emptying it.
    /// </summary>
    private static readonly string[] KnownDeclaringFiles =
    [
        "src/MeshWeaver.Hosting/MeshService.cs",
        "src/MeshWeaver.Mesh.Operations/MeshOperations.cs",
        "src/MeshWeaver.Mesh.Contract/Services/MeshNodeEditor.cs",
    ];

    /// <summary>One matched call: where it is, what it is posted on, and how it is addressed.</summary>
    private readonly record struct Site(
        string File, int Line, string Receiver, bool Declared, bool OffRouter, bool SelfDirected,
        string Target)
    {
        /// <summary>
        /// Whether this delivery's ORIGIN hub is one the file knows can be the router. Two ways to
        /// be: the bare DECLARED reference (the violation shape), or a SEAM derived from it — a
        /// <c>hub.ReadIssuingHub()</c> receiver, or a local bound to one. Both belong in the
        /// denominator, and it matters that they do: a denominator holding only the un-hopped sites
        /// would be the violation set wearing a denominator's name, so "0 of 0" and "0 of 24" would
        /// read identically. The second is the measurement; the first says nothing.
        /// </summary>
        public bool InScope => Declared || OffRouter;
    }

    [Fact]
    public void NoTargetedPostStaysOnAReceiverItsOwnFileDeclaredRouterCapable()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var sites = Scan(root);

        var declaring = sites.Select(s => s.File).Distinct(StringComparer.Ordinal).Count();
        var counted = sites.Where(InDenominator).ToList();
        output.WriteLine(
            $"{sites.Count} targeted .Post/.Observe sites in the {declaring} files that BOTH declare "
            + $"a router-capable receiver AND post with a target · {counted.Count} in the "
            + "denominator, of which "
            + $"{counted.Count(s => s.OffRouter)} are issued through a seam · "
            + $"{sites.Count(s => s.InScope && s.SelfDirected)} self-directed and "
            + $"{sites.Count(s => !s.InScope)} on an unrelated receiver, both excluded.");

        var violations = counted
            .Where(s => !s.OffRouter)
            .GroupBy(s => s.File, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var (file, count) in violations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — this file ALREADY calls "
                    + "NodeOperationIssuingHub()/ReadIssuingHub() on this very receiver, so it has "
                    + "declared that the receiver can be the ROUTER. Issue this delivery from the "
                    + "same seam: NodeOperationIssuingHub() for a node mutation, ReadIssuingHub() "
                    + "for a bounded request/response the target executes. Both are the identity "
                    + "function wherever the router is not reached. Do NOT add a line to "
                    + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a router-issued delivery "
                    + "was ADDED to a file that already carries the shape.");
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
            "A hub reference this file has ALREADY declared router-capable is still the origin of a "
            + "targeted delivery. That is #1140's remaining half: the message-keyed ratchet can only "
            + "see the LIFECYCLE slice of any one receiver, so #4477 hopped MeshOperations' "
            + "DisposeRequest and left five live sibling exchanges on the same field posting as "
            + "mesh/{id} — which is what #1140's receiver-side lines have been reporting all along.\n"
            + string.Join("\n", failures)
            + "\nOffending sites:\n"
            + string.Join("\n", counted.Where(s => !s.OffRouter)
                .Select(s => $"    {s.File}:{s.Line}  posted on '{s.Receiver}' → {s.Target}")));
    }

    /// <summary>
    /// Non-vacuity, in three halves that fail independently — the shape #4477 recorded as the reason
    /// the derivation and the ratchet must be two tests: when the derivation was deliberately broken
    /// there, the ratchet went GREEN over 25 sites with the message it existed for unguarded.
    ///
    /// <para>The first proves the DERIVATION reached production: files that declare a router-capable
    /// receiver were actually found, and the ones this defect class is named from are among them. An
    /// empty derivation makes the ratchet above pass over an unguarded tree.</para>
    ///
    /// <para>The second proves the CLASSIFIER can reach every verdict, by running the real scanning
    /// function over planted snippets containing one of each — including the exact pre-fix shape of
    /// the two sites this guard was written from, so the defect itself is pinned rather than a
    /// count.</para>
    ///
    /// <para>The third proves the scan reached the PRODUCTION tree and recognised the shape there. A
    /// planted-tree self-test cannot prove the scanner found <c>src/</c> (SourceScan's own remarks,
    /// #2844), and a production count cannot prove the classifier has a failing branch.</para>
    /// </summary>
    [Fact]
    public void TheDerivationIsSourcedTheClassifierReachesEveryVerdictAndSeesTheProductionTree()
    {
        var root = SourceScan.FindRepoRoot();

        // --- 1. the derivation ---------------------------------------------------------------
        var declared = DeclaringFiles(root);
        Assert.True(declared.Count > 0,
            "No file under " + string.Join(", ", ScannedRoots) + " calls NodeOperationIssuingHub() "
            + "or ReadIssuingHub() on a named receiver. The DENOMINATOR of this guard is EMPTY, so "
            + "it reports a clean tree while enforcing nothing — the exact failure mode it exists "
            + "to catch. The seams were renamed or moved; follow them, never relax the scan.");

        foreach (var file in KnownDeclaringFiles)
            Assert.True(declared.ContainsKey(file),
                $"{file} adopted a seam and is no longer seen to. Either it moved — follow it — or "
                + "the receiver walk stopped recognising the call, which would silently drop every "
                + "sibling post in that file out of the denominator. This is the file #4463 named "
                + "in production, so losing it is losing the measured instance.");

        Assert.Contains("hub", declared["src/MeshWeaver.Mesh.Operations/MeshOperations.cs"]);

        // --- 2. the classifier ---------------------------------------------------------------
        // A file with NO seam call declares nothing, so none of its posts is a site — that is the
        // stated limit of this guard, and it must be true rather than accidental.
        Assert.Empty(SitesIn(
            "no-seam.cs",
            "class P { void M(IMessageHub hub) => hub.Post(new Thing(), o => o.WithTarget(t)); }"));

        const string planted = """
            class Planted
            {
                void Declares(IMessageHub hub) => hub.NodeOperationIssuingHub().Post(new A(), o => o.WithTarget(t));

                void Violating(IMessageHub hub) => hub.Post(new B(), o => o.WithTarget(other));

                void Hopped(IMessageHub hub) => hub.ReadIssuingHub().Observe(new C(), o => o.WithTarget(other));

                void ViaAlias(IMessageHub hub)
                {
                    var issuing = hub.NodeOperationIssuingHub();
                    issuing.Post(new D(), o => o.WithTarget(other));
                }

                void SelfDirected(IMessageHub hub) => hub.Post(new E(), o => o.WithTarget(hub.Address));

                void NoTarget(IMessageHub hub) => hub.Post(new F());

                void OtherReceiver(IMessageHub notTheDeclaredOne) =>
                    notTheDeclaredOne.Post(new G(), o => o.WithTarget(other));
            }
            """;

        var sites = SitesIn("planted.cs", planted);
        // Every TARGETED call in a declaring file is a site; the target-less one is not.
        Assert.Equal(6, sites.Count);
        // The BARE declared reference — the violation shape and the self-directed one.
        Assert.Equal(2, sites.Count(s => s.Declared));
        // The three seam spellings: the call itself, the other seam, and a local bound to one.
        Assert.Equal(3, sites.Count(s => s.OffRouter));
        Assert.Equal(1, sites.Count(s => s.SelfDirected));
        // A post on an unrelated receiver says nothing about the router and stays OUT of scope.
        Assert.Equal(1, sites.Count(s => !s.InScope));
        var counted = sites.Where(InDenominator).ToList();
        Assert.Equal(4, counted.Count);
        Assert.Equal(1, counted.Count(s => !s.OffRouter));
        Assert.Equal(3, counted.Count(s => s.OffRouter));

        // 🚨 THE TWO SITES THIS GUARD WAS WRITTEN FROM, in their pre-fix spelling. Pinning the
        // SHAPE rather than a count is what makes a regression name the defect: if either of these
        // stops producing a violation, the guard has stopped seeing #1140's remaining half even
        // though its production count is still whatever it is.
        AssertOneViolatingSite(
            "MeshOperations: a sibling exchange on the field #4463 proved is the router",
            """
            class MeshOperations
            {
                void Recycle(IMessageHub hub) =>
                    hub.NodeOperationIssuingHub().Post(new DisposeRequest(), o => o.WithTarget(new Address(p)));

                void Patch(IMessageHub hub) =>
                    hub.Observe(new PatchDataRequest(r, j), o => o.WithTarget(new Address(p)));
            }
            """);
        AssertOneViolatingSite(
            "MeshNodeEditor: the write beside the hopped Move, on the same field",
            """
            class MeshNodeEditor
            {
                void Update(IMessageHub hub) =>
                    hub.Post(new DataChangeRequest(), o => o.WithTarget(new Address(CurrentPath)));

                void Move(IMessageHub hub)
                {
                    var issuingHub = hub.NodeOperationIssuingHub();
                    issuingHub.Post(new MoveNodeRequest(a, b), o => o.WithTarget(issuingHub.Address));
                }
            }
            """);

        // Comment and string masking, both sides. The same line is a site when it is code and
        // nothing when it is prose — the difference between measuring the tree and measuring the
        // remarks that describe it. MessageHub's own ROUTER_TRAFFIC log message names both seams
        // inside a string literal, and an unmasked scan reads that as a declaration.
        const string asCode = "hub.NodeOperationIssuingHub(); hub.Post(new B(), o => o.WithTarget(t));";
        Assert.Single(SitesIn("code.cs", asCode));
        Assert.Empty(SitesIn("prose.cs", "// " + asCode));
        Assert.Empty(SitesIn("literal.cs", "var s = \"hop with MeshExtensions.NodeOperationIssuingHub() first\";"
                                           + " hub.Post(new B(), o => o.WithTarget(t));"));

        // --- 3. the production tree ------------------------------------------------------------
        var found = Scan(root);
        Assert.True(found.Count > 0,
            "The scanner found NO targeted post in any declaring file. MeshService alone hops on "
            + "all five of its verbs, so this is a BROKEN SCAN reporting as a clean tree — the "
            + "failure mode SourceScan's remarks describe (#2844). Fix the scan; never soften the "
            + "guard that surfaced it.");
        Assert.True(found.Any(s => s.OffRouter),
            "The scanner classified EVERY production site as router-issued, so the seam test is "
            + "broken and every count is unreliable.");
        Assert.True(found.Count(InDenominator) > 0,
            "The production DENOMINATOR is EMPTY. The ratchet above then passes over nothing at "
            + "all, which is the one reading a ratchet must never be allowed to give — 'no "
            + "violations' and 'nothing was measured' would be the same sentence. Every hopped "
            + "delivery in a declaring file belongs in it (Site.InScope), so an empty denominator "
            + "means the seam or the receiver walk stopped being recognised.");
        // 🚨 The DECLARED branch must be reachable against production, not only against a planted
        // snippet. Today exactly one production site takes it — the #981 self-targeted inner create
        // in MeshExtensions — and it is excluded by the self-directed test alone. If `Declared`
        // could never be true here, the ratchet could never report anything and its green would be
        // structural.
        Assert.True(found.Any(s => s.Declared),
            "NO production site was classified as posted on a BARE declared receiver. That is the "
            + "only branch this ratchet can ever fail on, so it has become an instrument that "
            + "cannot fail. The receiver walk or the declaration scan stopped agreeing about a "
            + "file's own identifier — follow it, never soften the guard that surfaced it.");
        Assert.True(found.Any(s => s.SelfDirected),
            "The scanner found NO self-directed site. The #981 inner create in MeshExtensions is "
            + "one and is excluded by that test alone — if the test stops matching it, that site "
            + "silently becomes a violation and the next reader 'fixes' a line the allow file says "
            + "must not be touched.");
    }

    /// <summary>
    /// One planted snippet must yield exactly one COUNTED violation — i.e. the shape is seen AND
    /// reported. Named, so a regression says which shape stopped being visible.
    /// </summary>
    private static void AssertOneViolatingSite(string shape, string snippet)
    {
        var violations = SitesIn("planted-" + shape + ".cs", snippet)
            .Where(s => InDenominator(s) && !s.OffRouter)
            .ToList();
        Assert.True(violations.Count == 1,
            $"the '{shape}' shape produced {violations.Count} counted violations, not 1 — a "
            + "router-issued delivery this matcher cannot SEE is one this whole guard reports as "
            + $"clean. Snippet:\n{snippet}");
    }

    /// <summary>
    /// In the denominator when the delivery's origin hub is one this file declared router-capable —
    /// bare or already hopped (<see cref="Site.InScope"/>) — and it is not self-directed. A post on
    /// some OTHER receiver says nothing about the router; a self-directed one has no remedy.
    /// </summary>
    private static bool InDenominator(Site site) => site.InScope && !site.SelfDirected;

    /// <summary>
    /// The receivers each file DECLARES router-capable, read out of <c>src/</c> — never restated.
    /// A file with none is absent from the map, and none of its posts is a site.
    /// </summary>
    private static Dictionary<string, ImmutableHashSet<string>> DeclaringFiles(string root)
    {
        var map = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        foreach (var file in SourceScan.SourceFiles(root, ScannedRoots))
        {
            var declared = DeclaredIn(RouterOriginScan.ReadOrEmpty(file));
            if (!declared.IsEmpty)
                map[SourceScan.Relative(root, file)] = declared;
        }

        return map;
    }

    /// <summary>
    /// The receiver of every seam call in <paramref name="text"/>. Comments and string literals are
    /// masked first — <c>MessageHub</c>'s own ROUTER_TRAFFIC message names both seams inside a
    /// literal, and counting that would declare a receiver called <c>MeshExtensions</c>.
    /// </summary>
    private static ImmutableHashSet<string> DeclaredIn(string text) =>
        RouterOriginScan.SeamCall.IsMatch(text)
            ? DeclaredInMasked(SourceScan.MaskCommentsAndStrings(text))
            : ImmutableHashSet<string>.Empty;

    /// <summary>
    /// <see cref="DeclaredIn"/> over ALREADY-masked code, so the per-file scan masks once rather
    /// than once for the declaration pass and again for the call pass.
    /// </summary>
    private static ImmutableHashSet<string> DeclaredInMasked(string code) =>
        RouterOriginScan.SeamCallOnReceiver
            .Matches(code)
            .Select(m => m.Groups[1].Value)
            .ToImmutableHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<Site> Scan(string root) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .SelectMany(f => SitesIn(
                SourceScan.Relative(root, f), RouterOriginScan.ReadOrEmpty(f)))
            .ToList();

    /// <summary>
    /// Every TARGETED <c>.Post</c>/<c>.Observe</c> in <paramref name="text"/>, but only when the
    /// file declares at least one router-capable receiver. A post naming no target is not a site at
    /// all: it never leaves the posting hub, so neither <c>ROUTER_TRAFFIC</c> site reports it and
    /// the seam has nothing to move.
    /// </summary>
    private static IReadOnlyList<Site> SitesIn(string file, string text)
    {
        if (!RouterOriginScan.SeamCall.IsMatch(text))
            return [];

        var code = SourceScan.MaskCommentsAndStrings(text);
        var declared = DeclaredInMasked(code);
        if (declared.IsEmpty)
            return [];

        var aliases = RouterOriginScan.SeamAliasesIn(code);
        var sites = new List<Site>();

        foreach (Match call in RouterOriginScan.CallMarker.Matches(code))
        {
            var openParen = call.Index + call.Length - 1;
            var targets = RouterOriginScan.TargetsOf(code, openParen);
            if (targets.Count == 0)
                continue;

            var receiver = RouterOriginScan.Receiver(code, call.Index);
            sites.Add(new Site(
                file,
                RouterOriginScan.LineOf(code, call.Index),
                receiver,
                declared.Contains(receiver),
                RouterOriginScan.IsOffRouter(receiver, aliases),
                RouterOriginScan.IsSelfDirected(code, openParen, receiver),
                string.Join(" | ", targets)));
        }

        return sites;
    }
}

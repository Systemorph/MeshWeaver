using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet for the router-as-lifecycle-origin defect class (#1140, #4463): PRODUCTION
/// code may not issue a node- or hub-LIFECYCLE message from a hub that can be the root mesh hub
/// without hopping onto <c>MeshExtensions.NodeOperationIssuingHub()</c>.
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
/// <para>🚨 <b>And the first version of it had the trap one level along.</b> It carried a LITERAL
/// list of six node-CRUD request names, so <c>DisposeRequest</c> — a teardown, the most
/// router-hostile lifecycle message there is — was invisible to it. #4463 is the consequence:
/// <c>MeshOperations.RecycleCore</c> posted one straight off the DI-injected hub, only the runtime
/// ORIGIN detector saw it, and fixing that one line would have left every other lifecycle message
/// equally unguarded. Enumerating more names would buy exactly one more message. So the denominator
/// is now DERIVED FROM PRODUCTION instead of restated here — see below.</para>
///
/// <para><b>Why adopting the seam is never a behaviour change.</b>
/// <c>NodeOperationIssuingHub</c> returns the hub UNCHANGED unless its address type is the mesh
/// type — it is the identity function for every portal, session, layout, import and per-node hub.
/// So a site that is already off the router is byte-for-byte unaffected, and a site that is not is
/// corrected. That is what makes "adopt it everywhere" a rule rather than a judgement call, and it
/// is why this ratchet can demand the seam without asking each author to prove which hub reaches
/// their code.</para>
///
/// <para><b>THE DENOMINATOR, and why it is the right one.</b> A message is lifecycle when the
/// FRAMEWORK ITSELF registers a lifecycle handler for it, and there are exactly two such
/// registrations. Both are read out of <c>src/</c> at scan time; neither is spelled here.
/// <list type="number">
///   <item><b>Hub lifecycle</b> — every <c>Register&lt;T&gt;(…)</c> in <c>MessageHub</c>'s
///     CONSTRUCTOR: the messages a hub answers <i>because it is a hub</i> (<c>DisposeRequest</c>,
///     <c>ShutdownRequest</c>, <c>PingRequest</c>, <c>InitializeHubRequest</c>).</item>
///   <item><b>Node lifecycle</b> — every <c>.WithHandler&lt;T&gt;(…)</c> in
///     <c>MeshExtensions.WithNodeOperationHandlers</c>: the messages the mesh's off-router node-CRUD
///     EXECUTION hub answers, which is what makes ONE seam the answer for all of them.</item>
/// </list>
/// This is a rule rather than a list because <b>you cannot add a lifecycle message without passing
/// through one of those two registrations</b> — a handler is what makes a message lifecycle in the
/// first place. A marker attribute, or a name added to a test, can be forgotten; a handler cannot,
/// because without it the message does nothing. The derivation caught a verb the hand-written list
/// had already missed (<c>ValidateDeleteRequest</c>) on its first run.</para>
///
/// <para><b>Sourced exclusions, also not restated.</b> The derived verbs are filtered through
/// <see cref="RouterTrafficRule"/> itself — the same pure predicate the two runtime detector sites
/// evaluate — so a message the production rule declares to be routing's OWN duty rather than work
/// (today: <c>HeartBeatEvent</c>, which <c>WithNodeOperationHandlers</c> also registers) leaves the
/// denominator automatically, and a future exclusion added there is inherited rather than copied.
/// Every derived name must also RESOLVE to a real type, so a rename or a move reddens this guard
/// instead of silently shrinking what it measures.</para>
///
/// <para><b>What is matched.</b> A <c>.Observe(…)</c> / <c>.Post(…)</c> call whose FIRST argument is
/// a lifecycle message — either constructed inline (<c>new CreateNodeRequest(node)</c>) or hoisted
/// into a local earlier in the file (<c>var request = new DeleteNodeRequest(path); …
/// IssuingHub.Observe(request, …)</c>, which is <c>MeshService</c>'s own shape and which a
/// constructor-anchored scan would have classified on the wrong statement). The receiver is then
/// read as a primary expression and compared against the seam.</para>
///
/// <para>🚨 <b>One structural exclusion: a SELF-DIRECTED hub-lifecycle post.</b> A hub telling
/// ITSELF to dispose, initialise or shut down — no target, or <c>o.WithTarget(thatHub.Address)</c> —
/// is out of the denominator, and NOT because the detector is quiet about it. It is because <b>the
/// remedy does not apply</b>: the seam's whole effect is to issue the delivery from a DIFFERENT hub,
/// which for a self-directed teardown would send it to the wrong hub and change what is torn down.
/// A rule whose fix is nonsense at a site must not report that site. This is the shape
/// <c>NodeTypeRebindWatcher</c>, the stale-build convergence, the overlay self-heal and
/// <c>LayoutAreaHost</c>'s own <c>InitializeHubRequest</c> all use. The NODE family is deliberately
/// NOT excluded this way: a target-less <c>CreateNodeRequest</c> on the router is the literal prod
/// 2026-06-11 wedge (<c>CreateNodeRequest@mesh/&lt;self&gt; stale &gt;60s</c>), the seam DOES fix it
/// (the request then executes on the node-operation hub's block, not the router's), and the one
/// seeded allowance is exactly that shape.</para>
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
///   <item>Messages the framework registers no lifecycle handler for. #1140's evidence also names
///     <c>ClickedEvent</c>, <c>UnsubscribeRequest</c> and <c>PatchDataChangeRequest</c>; those have
///     no single seam to hop onto, and a marker that fired on the router's own routing duties is
///     the one that gets muted — the argument <see cref="RouterTrafficRule"/> itself makes for
///     keying on the delivery's ends rather than the handling hub.</item>
/// </list></para>
///
/// <para><b>The tolerances were measured, not guessed.</b> Over <c>src/</c> at the time of writing
/// this matches 33 lifecycle post sites, 4 of them self-directed hub lifecycle, leaving 29 in the
/// denominator. A scan that required the request to be constructed INSIDE the call's argument list
/// would miss 7 of the 33 — every verb of <c>MeshService</c>, the reference implementation, plus the
/// copy helper's hoisted request. A seam test that accepted only the literal
/// <c>NodeOperationIssuingHub()</c> text would misclassify the 11 that hoist it once per operation
/// (<c>var issuingHub = hub.NodeOperationIssuingHub();</c>,
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
    /// Where the HUB-lifecycle family is declared: the <c>Register&lt;T&gt;</c> calls in
    /// <c>MessageHub</c>'s constructor. Read, never restated — see the class remarks.
    /// </summary>
    private const string HubLifecycleSourceFile = "src/MeshWeaver.Messaging.Hub/MessageHub.cs";

    /// <summary>Where the NODE-lifecycle family is declared.</summary>
    private const string NodeLifecycleSourceFile = "src/MeshWeaver.Mesh.Contract/MeshExtensions.cs";

    private const string AllowFileName = "RouterNodeOperationOriginSites.allow";

    /// <summary>
    /// The request/response and fire-and-forget entry points, tolerant of an explicit response type
    /// argument (<c>Observe&lt;CreateNodeResponse&gt;(</c>) and of the line break C# style puts
    /// between the receiver and the call.
    /// </summary>
    private static readonly Regex CallMarker =
        new(@"\.\s*(?:Observe|Post)\s*(?:<[^<>()]*>\s*)?\(", RegexOptions.Compiled);

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

    /// <summary>The <c>o.WithTarget(<i>expr</i>)</c> inside a post's options lambda.</summary>
    private static readonly Regex TargetOption =
        new(@"\bWithTarget\s*\(", RegexOptions.Compiled);

    /// <summary>The hub's own constructor-time lifecycle registrations.</summary>
    private static readonly Regex HubRegistration =
        new(@"\bRegister<([A-Za-z_][A-Za-z0-9_]*)>\s*\(", RegexOptions.Compiled);

    /// <summary>The node-operation execution hub's handler registrations.</summary>
    private static readonly Regex HandlerRegistration =
        new(@"\.\s*WithHandler<([A-Za-z_][A-Za-z0-9_]*)>\s*\(", RegexOptions.Compiled);

    private static readonly Regex HubConstructorSignature =
        new(@"\bpublic\s+MessageHub\s*\(", RegexOptions.Compiled);

    private static readonly Regex NodeOperationRegistrarSignature =
        new(@"\bWithNodeOperationHandlers\s*\(\s*this\s+MessageHubConfiguration", RegexOptions.Compiled);

    /// <summary>
    /// The two lifecycle families, as derived from production, plus the matchers built over them.
    /// A hub-lifecycle post is excluded when it is self-directed (the seam's remedy would misdeliver
    /// it); a node-lifecycle post never is.
    /// </summary>
    private sealed class LifecycleVerbs
    {
        public LifecycleVerbs(ImmutableHashSet<string> hub, ImmutableHashSet<string> node)
        {
            Hub = hub;
            Node = node;
            All = hub.Union(node);
            var alternation = string.Join("|", All.Order(StringComparer.Ordinal));
            NewRequest = new Regex(@"\bnew\s+(" + alternation + @")\b");
            RequestLocal = new Regex(
                @"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+(" + alternation + @")\b");
        }

        public ImmutableHashSet<string> Hub { get; }

        public ImmutableHashSet<string> Node { get; }

        public ImmutableHashSet<string> All { get; }

        /// <summary>A lifecycle message constructed inline in a call's argument list.</summary>
        public Regex NewRequest { get; }

        /// <summary>A local or field bound to a freshly constructed lifecycle message.</summary>
        public Regex RequestLocal { get; }
    }

    /// <summary>One matched call: where it is, which verb, and how it is addressed.</summary>
    private readonly record struct Site(
        string File, int Line, bool OffRouter, string Receiver, string Verb, bool SelfDirected);

    [Fact]
    public void NoNewProductionSiteIssuesLifecycleWorkFromTheRouter()
    {
        var root = SourceScan.FindRepoRoot();
        var allowed = SourceScan.ReadAllowFile(Path.Combine(root, "test", AllowFileName), AllowFileName);
        var verbs = DeriveVerbs(root);

        output.WriteLine(
            $"Lifecycle verbs derived from src/: hub [{string.Join(", ", verbs.Hub.Order(StringComparer.Ordinal))}]"
            + $" · node [{string.Join(", ", verbs.Node.Order(StringComparer.Ordinal))}]");

        var sites = Scan(root, verbs);
        var counted = sites.Where(s => InDenominator(s, verbs)).ToList();
        output.WriteLine(
            $"{counted.Count} lifecycle post sites in the denominator ({sites.Count - counted.Count} "
            + "self-directed hub-lifecycle posts excluded).");

        var violations = counted
            .Where(s => !s.OffRouter)
            .GroupBy(s => s.File, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var failures = new List<string>();

        foreach (var (file, count) in violations.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var budget))
                failures.Add(
                    $"  NEW SITE   {file} ({count}) — issue the message from "
                    + "hub.NodeOperationIssuingHub() (or hub.ReadIssuingHub() for a one-shot read). "
                    + "It returns the hub unchanged unless that hub is the ROUTER, so this is a "
                    + "no-op wherever the router is not reached. Do NOT add a line to "
                    + AllowFileName + ".");
            else if (count > budget)
                failures.Add(
                    $"  MORE       {file} ({count} > {budget} allowed) — a router-issued lifecycle "
                    + "message was ADDED to a file that already carries the shape.");
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
            "Production code that issues node- or hub-LIFECYCLE work from the root mesh hub puts the "
            + "ROUTER on an end of the delivery — and, when the request is target-less, EXECUTES it "
            + "on the routing action block. That is #1140, which was filed four times and ran to "
            + "41,087 error lines before anyone could name a call site, and #4463, which slipped "
            + "past the first version of this guard because DisposeRequest was not on a literal "
            + "list. Hop onto MeshExtensions.NodeOperationIssuingHub().\n"
            + string.Join("\n", failures)
            + "\nOffending sites:\n"
            + string.Join("\n", counted.Where(s => !s.OffRouter)
                .Select(s => $"    {s.File}:{s.Line}  {s.Verb} posted on '{s.Receiver}'")));
    }

    /// <summary>
    /// Non-vacuity, in three halves that fail independently.
    ///
    /// <para>The first proves the DERIVATION reached both production registrations, that every name
    /// it produced is a real type, and that the family it yields is the lifecycle family rather than
    /// whatever a broken regex happened to capture. A guard whose denominator silently emptied would
    /// report a clean tree while enforcing nothing — the exact shape of the defect it exists to
    /// catch.</para>
    ///
    /// <para>The second proves the CLASSIFIER can reach every verdict, by running the real scanning
    /// functions over a planted snippet that contains one of each. Without it a seam test that
    /// accidentally matched everything would report an empty violation set and the ratchet above
    /// would pass having blessed the tree.</para>
    ///
    /// <para>The third proves the scan reached the PRODUCTION tree and recognised the shape there.
    /// The three fail independently and only together mean anything — a planted-tree self-test
    /// cannot prove the scanner found <c>src/</c> (SourceScan's own remarks, #2844), and a
    /// production count cannot prove the classifier has a failing branch.</para>
    /// </summary>
    [Fact]
    public void TheDerivationIsSourcedTheScannerReachesEveryVerdictAndSeesTheProductionTree()
    {
        var root = SourceScan.FindRepoRoot();
        var verbs = DeriveVerbs(root);

        // --- 1. the derivation ---------------------------------------------------------------
        Assert.True(verbs.Hub.Count > 0,
            $"No Register<T>(…) call was found in MessageHub's constructor ({HubLifecycleSourceFile}). "
            + "The HUB-lifecycle half of this guard's denominator is EMPTY, so DisposeRequest — the "
            + "message #4463 was about — is unguarded again while this test reports green. The "
            + "constructor or the registration spelling moved; follow it, never relax the scan.");
        Assert.True(verbs.Node.Count > 0,
            $"No .WithHandler<T>(…) call was found in WithNodeOperationHandlers ({NodeLifecycleSourceFile}). "
            + "The NODE-lifecycle half of the denominator is EMPTY — every CreateNodeRequest in the "
            + "tree is unguarded while this test reports green.");
        Assert.Contains(nameof(DisposeRequest), verbs.Hub);
        Assert.Contains(nameof(PingRequest), verbs.Hub);
        Assert.Contains(nameof(CreateNodeRequest), verbs.Node);
        Assert.Contains(nameof(DeleteNodeRequest), verbs.Node);
        // Sourced from the production predicate, not restated: HeartBeatEvent IS registered by
        // WithNodeOperationHandlers and RouterTrafficRule declares it routing's own liveness, so the
        // filter must have dropped it. If RouterTrafficRule stops excluding it, this flips and the
        // reader is told where the disagreement is rather than silently inheriting it.
        Assert.DoesNotContain(nameof(HeartBeatEvent), verbs.All);
        Assert.Null(RouterTrafficRule.RoleOf(
            "portal", AddressExtensions.MeshType, new HeartBeatEvent(), isResponse: false));

        // --- 2. the classifier ---------------------------------------------------------------
        const string planted = """
            class Planted
            {
                IObservable<int> Violating(IMessageHub hub) =>
                    hub.Observe(new CreateNodeRequest(node), o => o.WithTarget(t)).Select(_ => 1);

                void ViolatingTeardown(IMessageHub hub) =>
                    hub.Post(new DisposeRequest { Reason = r }, o => o.WithTarget(new Address(p)));

                IObservable<int> Compliant(IMessageHub hub) =>
                    hub.NodeOperationIssuingHub()
                        .Observe(new DeleteNodeRequest(path), o => o.WithTarget(t)).Select(_ => 1);

                IObservable<int> ViaAlias(IMessageHub hub)
                {
                    var issuing = hub.NodeOperationIssuingHub();
                    var request = new MoveNodeRequest(from, to);
                    return issuing.Observe(request, o => o.WithTarget(t)).Select(_ => 1);
                }

                void SelfDirectedTeardown(IMessageHub instanceHub) =>
                    instanceHub.Post(new DisposeRequest { Reason = r },
                        o => o.WithTarget(instanceHub.Address));

                void SelfDirectedInit(IMessageHub streamHub) =>
                    streamHub.Post(new InitializeHubRequest());

                IObservable<int> NotARequest(IMessageHub hub) =>
                    hub.Observe(somethingElse, o => o.WithTarget(t)).Select(_ => 1);
            }
            """;

        var plantedSites = SitesIn("planted.cs", planted, verbs);

        Assert.Equal(6, plantedSites.Count);
        var plantedCounted = plantedSites.Where(s => InDenominator(s, verbs)).ToList();
        Assert.Equal(4, plantedCounted.Count);
        Assert.Equal(2, plantedCounted.Count(s => !s.OffRouter));
        Assert.Equal(2, plantedCounted.Count(s => s.OffRouter));
        Assert.Equal(
            new[] { nameof(CreateNodeRequest), nameof(DisposeRequest) },
            plantedCounted.Where(s => !s.OffRouter).Select(s => s.Verb)
                .Order(StringComparer.Ordinal).ToArray());
        // Both self-directed shapes leave the denominator, and BOTH spellings must: the explicit
        // o.WithTarget(thatHub.Address) and the target-less post.
        Assert.Equal(2, plantedSites.Count(s => s.SelfDirected));
        // …but a self-directed NODE-lifecycle post stays IN — it is the prod 2026-06-11 wedge, and
        // the seam does fix it by moving execution off the router's block.
        var plantedNodeSelfPost = SitesIn(
            "planted-self-crud.cs",
            "class P { void M(IMessageHub hub) => hub.Observe(new CreateNodeRequest(n), "
            + "o => o.WithTarget(hub.Address)); }",
            verbs);
        Assert.True(InDenominator(Assert.Single(plantedNodeSelfPost), verbs));

        // Comment masking, both sides. The same line is a site when it is code and nothing when it
        // is prose — which is the difference between measuring the tree and measuring the remarks
        // that describe it. This repo's remarks quote these shapes verbatim.
        const string asCode = "var x = hub.Observe(new CreateNodeRequest(n), o => o.WithTarget(t));";
        Assert.Single(SitesIn("code.cs", asCode, verbs));
        Assert.Empty(SitesIn("prose.cs", "// " + asCode, verbs));
        Assert.Empty(SitesIn("literal.cs", "var s = \"" + asCode.Replace("\"", "") + "\";", verbs));

        // --- 3. the production tree ------------------------------------------------------------
        var found = Scan(root, verbs);

        Assert.True(found.Count > 0,
            "The scanner found NO lifecycle post site anywhere under "
            + string.Join(", ", ScannedRoots)
            + ". src/ cannot be free of node CRUD, so this is a BROKEN SCAN reporting as a clean "
            + "tree — the failure mode SourceScan's remarks describe (#2844). Fix the scan; never "
            + "soften the guard that surfaced it.");
        Assert.True(found.Any(s => s.OffRouter),
            "The scanner classified EVERY production site as router-issued. MeshService alone hops "
            + "on all five of its verbs, so the seam test is broken and every count is unreliable.");
        Assert.True(found.Any(s => verbs.Hub.Contains(s.Verb)),
            "The scanner found node-CRUD sites but NO hub-lifecycle site (DisposeRequest, "
            + "PingRequest, …). src/ posts both — PackageInstaller.SettleRetypedRoot alone posts a "
            + "DisposeRequest and a PingRequest through the seam — so the half of the denominator "
            + "#4463 added is measuring nothing.");
    }

    /// <summary>
    /// Whether a matched site is in the denominator: a self-directed HUB-lifecycle post is not,
    /// because the seam's remedy — issue it from a different hub — would misdeliver it.
    /// </summary>
    private static bool InDenominator(Site site, LifecycleVerbs verbs) =>
        !(verbs.Hub.Contains(site.Verb) && site.SelfDirected);

    /// <summary>
    /// The lifecycle families, read out of the two production registrations. Every derived name
    /// must resolve to a loadable type, and any the production <see cref="RouterTrafficRule"/>
    /// declares to be routing's own duty is dropped.
    /// </summary>
    private static LifecycleVerbs DeriveVerbs(string root)
    {
        var hub = RegisteredIn(
            Path.Combine(root, HubLifecycleSourceFile), HubConstructorSignature, HubRegistration);
        var node = RegisteredIn(
            Path.Combine(root, NodeLifecycleSourceFile),
            NodeOperationRegistrarSignature, HandlerRegistration);
        return new LifecycleVerbs(WorkMessages(hub), WorkMessages(node));
    }

    /// <summary>
    /// The generic type arguments of every <paramref name="registration"/> inside the body that
    /// follows <paramref name="signature"/>, brace-matched. Reading the BODY rather than the file
    /// is what keeps the generic <c>Register&lt;TMessage&gt;</c> DECLARATIONS out of the family.
    /// </summary>
    private static ImmutableHashSet<string> RegisteredIn(
        string file, Regex signature, Regex registration)
    {
        Assert.True(File.Exists(file),
            $"{file} is the source this guard DERIVES its denominator from, and it is not there. "
            + "The file moved; point this guard at it. Never fall back to a literal list — that is "
            + "the defect #4463 was.");

        var code = SourceScan.MaskCommentsAndStrings(File.ReadAllText(file));
        var match = signature.Match(code);
        Assert.True(match.Success,
            $"Could not find '{signature}' in {file}. The declaration this guard reads its "
            + "denominator from was renamed or restructured — follow it. A guard whose derivation "
            + "silently returns nothing reports a clean tree while enforcing nothing.");

        return registration.Matches(BodyAfter(code, match.Index + match.Length))
            .Select(m => m.Groups[1].Value)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>The brace-matched block starting at the first <c>{</c> at or after <paramref name="from"/>.</summary>
    private static string BodyAfter(string code, int from)
    {
        var start = code.IndexOf('{', from);
        if (start < 0) return string.Empty;
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[start..(i + 1)];
        }

        return code[start..];
    }

    /// <summary>
    /// The subset of <paramref name="names"/> that the production <see cref="RouterTrafficRule"/>
    /// treats as WORK rather than as routing's own duty — asked of the rule itself, so an exclusion
    /// added there is inherited instead of copied. Every name must resolve to a type: a rename that
    /// silently dropped a verb would shrink the denominator without reddening anything.
    /// </summary>
    private static ImmutableHashSet<string> WorkMessages(ImmutableHashSet<string> names)
    {
        var assemblies = new[]
        {
            typeof(RouterTrafficRule).Assembly,   // MeshWeaver.Messaging.Contract
            typeof(MessageHub).Assembly,          // MeshWeaver.Messaging.Hub
            typeof(MeshExtensions).Assembly,      // MeshWeaver.Mesh.Contract
        };

        return names
            .Where(name =>
            {
                var type = assemblies.Select(a => Resolve(a, name)).FirstOrDefault(t => t is not null);
                Assert.True(type is not null,
                    $"The derivation produced '{name}', but no type of that name exists in "
                    + string.Join(", ", assemblies.Select(a => a.GetName().Name))
                    + ". Either the registration scan is capturing something that is not a message "
                    + "type, or the type moved to an assembly this guard does not anchor on. Both "
                    + "shrink the denominator silently, which is why this is a failure and not a "
                    + "filter.");
                // A non-response delivery from the router to a non-router address: the shape every
                // site in this scan produces. `null` means the rule calls it routing's own duty.
                return RouterTrafficRule.RoleOf(
                    "portal",
                    AddressExtensions.MeshType,
                    RuntimeHelpers.GetUninitializedObject(type!),
                    isResponse: false) is not null;
            })
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>The single type named <paramref name="name"/> in <paramref name="assembly"/>,
    /// internal ones included, or <c>null</c>.</summary>
    private static Type? Resolve(Assembly assembly, string name)
    {
        try
        {
            return assembly.GetTypes().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.FirstOrDefault(t => t is not null && string.Equals(t.Name, name, StringComparison.Ordinal));
        }
    }

    private static IReadOnlyList<Site> Scan(string root, LifecycleVerbs verbs) =>
        SourceScan.SourceFiles(root, ScannedRoots)
            .SelectMany(f => SitesIn(SourceScan.Relative(root, f), ReadOrEmpty(f), verbs))
            .ToList();

    private static string ReadOrEmpty(string path)
    {
        // A file a concurrent build is writing is not evidence.
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; }
    }

    /// <summary>
    /// Every <c>.Observe</c>/<c>.Post</c> call in <paramref name="text"/> whose first argument is a
    /// lifecycle message, classified by whether its receiver is one of the off-router seams and by
    /// whether the post is self-directed. Comments and string literals are masked first, so remarks
    /// quoting the shape are not counted.
    /// </summary>
    private static IReadOnlyList<Site> SitesIn(string file, string text, LifecycleVerbs verbs)
    {
        if (!verbs.All.Any(t => text.Contains(t, StringComparison.Ordinal)))
            return [];

        var code = SourceScan.MaskCommentsAndStrings(text);

        var requestLocals = new Dictionary<string, (int At, string Verb)>(StringComparer.Ordinal);
        foreach (Match m in verbs.RequestLocal.Matches(code))
            if (!requestLocals.ContainsKey(m.Groups[1].Value))
                requestLocals[m.Groups[1].Value] = (m.Index, m.Groups[2].Value);

        var seamAliases = SeamAlias.Matches(code)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var sites = new List<Site>();
        foreach (Match call in CallMarker.Matches(code))
        {
            var openParen = call.Index + call.Length - 1;
            var argument = SourceScan.FirstArgument(code, openParen).Trim();

            string verb;
            var constructed = verbs.NewRequest.Match(argument);
            if (constructed.Success)
            {
                verb = constructed.Groups[1].Value;
            }
            else
            {
                // The hoisted spelling: the request was built into a local a few lines up.
                var head = argument.Split('.')[0].Trim();
                if (!BareIdentifier.IsMatch(head)
                    || !requestLocals.TryGetValue(head, out var bound)
                    || bound.At >= call.Index)
                    continue;
                verb = bound.Verb;
            }

            var receiver = Receiver(code, call.Index);
            var offRouter = SeamCall.IsMatch(receiver) || IsSeamAlias(receiver, seamAliases);
            sites.Add(new Site(
                file, LineOf(code, call.Index), offRouter, receiver, verb,
                IsSelfDirected(code, openParen, receiver)));
        }

        return sites;
    }

    /// <summary>
    /// Whether the post names no target at all, or names only the RECEIVER's own address — the two
    /// spellings of "this hub is telling itself", where the issuing seam has nothing to move and
    /// adopting it would change which hub the message reaches.
    /// </summary>
    private static bool IsSelfDirected(string code, int openParen, string receiver)
    {
        var arguments = ArgumentList(code, openParen);
        var targets = TargetOption.Matches(arguments)
            .Select(m => Collapse(SourceScan.FirstArgument(arguments, m.Index + m.Length - 1)))
            .ToList();
        return targets.Count == 0
               || targets.All(t => string.Equals(t, Collapse(receiver) + ".Address", StringComparison.Ordinal));
    }

    /// <summary>The whole argument list of the call whose open paren is at <paramref name="openParen"/>.</summary>
    private static string ArgumentList(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            if (code[i] is '(' or '[' or '{') depth++;
            else if (code[i] is ')' or ']' or '}' && --depth == 0) return code[(openParen + 1)..i];
        }

        return code[(openParen + 1)..];
    }

    private static string Collapse(string expression) =>
        string.Concat(expression.Where(c => !char.IsWhiteSpace(c)));

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

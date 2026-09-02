#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A NACK about an oversized message must not BE one — and the two seams that make that true
/// for EVERY construction site must both stay armed.</b> Issues #1890, #2885, #3044/#3049, #3056,
/// #3104.
///
/// <para><b>What is being protected.</b> <see cref="MeshWeaver.Messaging.DeliveryFailure"/> embeds
/// the ORIGINAL delivery, payload and all, and travels the SAME transport back to the sender. A
/// failure report about a 37 MB message is therefore itself a 37 MB message and dies at exactly the
/// wall it is reporting on — silently, leaving the producer with neither the message nor the
/// report.</para>
///
/// <para><b>Why this is a GUARD and not a code review.</b> The rule shipped twice as a hand-applied
/// call at ONE site each (<c>RoutingGrain.PostFailure</c> #1890,
/// <c>OrleansRoutingService.SendDeliveryFailure</c> #2885), and the site that then took a production
/// pod down was a THIRD one that had never been told: <c>MessageService.ReportFailure</c>
/// (#3044/#3049). With ~25 <c>new DeliveryFailure(delivery)</c> sites in this repository, "remember
/// to strip" was never a control. Twice it drifted; twice it was found in production.</para>
///
/// <para>🚨 <b>So the rule is not enforced per call site, and a guard that demanded a call at every
/// site would be enforcing the wrong thing.</b> It is enforced at TWO structural seams, and every
/// construction site — including the ones no one has written yet — is covered by them:</para>
/// <list type="number">
///   <item><b>The construction invariant</b> (#3056). <c>DeliveryFailure.Delivery</c>'s init accessor
///     runs the strip, so any site whose payload is already <c>RawJson</c> is safe by the time the
///     record exists. That is every post-packaging NACK, which by #1485 is every one the routers
///     raise.</item>
///   <item><b>The packaging seam</b> (#3104). <c>MessageDelivery.Package</c> strips a
///     <c>DeliveryFailure</c>'s echo before serialising it. The construction invariant cannot cover a
///     TYPED payload — measuring one needs <c>JsonSerializerOptions</c> and the record has none — and
///     typed is exactly what every PRE-packaging NACK carries. <c>AccessControlPipeline</c> is the
///     sharpest case: <c>[RequiresPermission]</c> is an attribute on the message TYPE, so a
///     permission check cannot run against <c>RawJson</c> at all, and a denial echoed the whole body
///     back.</item>
/// </list>
///
/// <para>Between them: whatever the payload's shape, and whichever of the ~25 sites built the report,
/// it is stripped exactly when carrying it is what would lose it — and never otherwise, because an
/// echo that fits is diagnostic and an unconditional strip would destroy it.</para>
///
/// <para>🚨 <b>Each assertion here carries a CONTROL ARM, because the silent failure of a
/// source-scanning guard is that its matcher stops matching.</b> A guard whose subject was renamed
/// or moved reports green having checked nothing, which is indistinguishable from a clean tree — the
/// same reading that makes an absent required check look like a passing one.
/// <see cref="TheGuardStillHasSubjectsToProtect"/> fails when
/// <c>new DeliveryFailure(</c> can no longer be found in <c>src/</c> at all, and every seam assertion
/// fails when the code it is reading about is missing rather than merely non-compliant.</para>
/// </summary>
public class DeliveryFailureEchoStripGuard(ITestOutputHelper output)
{
    /// <summary>Production roots. A NACK raised in a test never meets a transport.</summary>
    private static readonly string[] ScannedRoots = ["src"];

    private const string ConstructionInvariantSource = "src/MeshWeaver.Messaging.Contract/Events.cs";
    private const string PackagingSeamSource = "src/MeshWeaver.Messaging.Hub/MessageDelivery.cs";
    private const string RoutingSource = "src/MeshWeaver.Mesh.Contract/MeshBuilder.cs";

    /// <summary>The strip, in either of its overloads — the name is the whole coupling.</summary>
    private const string Strip = "WithoutOversizedPayload";

    /// <summary>
    /// <c>JsonSerializer.Serialize</c>, whitespace-tolerant. 🚨 A literal would be blind to the call
    /// spelled across lines, which is how <see cref="ImpersonationScopeSiteRatchetGuard"/>'s matcher
    /// hid 19 real sites (#2441) — a reformat must never silently disarm a guard.
    /// </summary>
    private static readonly Regex SerializeCall =
        new(@"JsonSerializer\s*\.\s*Serialize", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary><c>new DeliveryFailure(</c>, whitespace-tolerant.</summary>
    private static readonly Regex ConstructionSite =
        new(@"new\s+DeliveryFailure\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The <c>Delivery</c> init accessor running the strip. Matched as "the initializer of THIS
    /// property contains the call", not "the file mentions the call" — <c>Events.cs</c>'s own remarks
    /// name <c>WithoutOversizedPayload</c>, and a whole-file match would keep passing after the
    /// invariant itself was deleted. A grep hit is not a binder.
    /// </summary>
    private static readonly Regex ConstructionInvariant =
        new(@"IMessageDelivery\s+Delivery\s*\{\s*get\s*;\s*init\s*;\s*\}\s*=[^;]*" + Strip,
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A declaration of the packaging seam itself.</summary>
    private static readonly Regex PackagingSeamDeclaration =
        new(@"IMessageDelivery\s+Package\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The routing handler handing the router a PACKAGED delivery. Pinned as the pair, not as a bare
    /// <c>.Package(</c>: the seam only protects the mesh while routing actually goes through it, and
    /// <c>DeliverMessage(delivery)</c> would bypass it while still compiling and still passing every
    /// functional test.
    /// </summary>
    private static readonly Regex RoutesThroughSeam =
        new(@"DeliverMessage\s*\(\s*delivery\s*\.\s*Package\s*\(",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 🚨 SEAM 1 — the construction invariant (#3056). Every site whose payload is already
    /// <c>RawJson</c> is covered by the record itself, so no call site has to remember.
    /// </summary>
    [Fact]
    public void TheConstructionInvariantStrapsEveryConstructionSite()
    {
        var code = Masked(ConstructionInvariantSource);

        Assert.True(
            ConstructionInvariant.IsMatch(code),
            $"{ConstructionInvariantSource} no longer applies {Strip} in DeliveryFailure.Delivery's "
            + "init accessor.\n"
            + "That accessor is what makes the rule structural instead of a thing ~25 construction "
            + "sites have to remember — and it exists because 'remember to strip' already failed "
            + "twice in production (#1890 and #2885 applied it at one site each; #3044/#3049 died at "
            + "a third that had never been told).\n"
            + "If the invariant moved, move this assertion with it in the SAME change. Do not delete "
            + "it: a NACK that carries the payload it is reporting on is lost in silence, and the "
            + "sender is left waiting out its timeout with nothing to show for it.");
    }

    /// <summary>
    /// 🚨 SEAM 2 — the packaging seam (#3104), and the ORDER is the whole assertion. Stripping after
    /// the serialize would compile, pass every functional test, and do nothing: the allocation that
    /// threw <c>OutOfMemoryException</c> in production is the serialize itself.
    /// </summary>
    [Fact]
    public void ThePackagingSeamStripsBeforeItSerializes()
    {
        var code = Masked(PackagingSeamSource);
        var serialize = SerializeCall.Match(code);

        // 🚨 The control arm. No serialize in this file means the seam moved, and every ordering
        // claim below would be vacuously true — "nothing to check" reported as "checked and clean".
        Assert.True(
            serialize.Success,
            $"{PackagingSeamSource} contains no JsonSerializer.Serialize call. This guard reads that "
            + "call as the moment a delivery becomes its wire form, so its absence means the "
            + "packaging seam has moved and this guard is now measuring nothing. Follow the seam — "
            + "do not soften the assertion that surfaced it (#2844).");

        var strip = code.IndexOf(Strip, StringComparison.Ordinal);
        Assert.True(
            strip >= 0 && strip < serialize.Index,
            $"{PackagingSeamSource} does not apply {Strip} to a DeliveryFailure's echoed delivery "
            + "BEFORE serializing it"
            + (strip < 0 ? " (the call is absent entirely)." : " (the call is there, but AFTER).")
            + "\nOrder is the whole point: the serialize IS the allocation that threw "
            + "OutOfMemoryException on a production pod (Utf8JsonWriter.TranscodeAndWriteRawValue → "
            + "SharedArrayPool.Rent → GC.AllocateNewArray, #3049). Stripping afterwards is stripping "
            + "a report that has already been lost.\n"
            + "This is the ONLY place a TYPED payload can be measured — DeliveryFailure's own "
            + "constructor has no JsonSerializerOptions and therefore cannot see one (#3104) — so "
            + "without it every pre-packaging NACK echoes its body back verbatim, "
            + "AccessControlPipeline's permission denials above all.");
    }

    /// <summary>
    /// The seam only protects the mesh while traffic actually goes through it. A routing handler that
    /// stopped packaging would leave the strip armed and unreachable — a merged guard can be
    /// unreachable in production (#2813).
    /// </summary>
    [Fact]
    public void TheRoutingHandlerStillGoesThroughThePackagingSeam()
    {
        var code = Masked(RoutingSource);

        Assert.True(
            code.Contains("DeliverMessage", StringComparison.Ordinal),
            $"{RoutingSource} no longer hands anything to IRoutingService.DeliverMessage — the "
            + "routing handler this guard reads has moved, so the assertion below would pass having "
            + "checked nothing. Follow it.");
        Assert.True(
            RoutesThroughSeam.IsMatch(code),
            $"{RoutingSource} no longer routes through delivery.Package(...). Every cross-boundary "
            + "delivery reaches a transport through that one call, which is why the oversized-echo "
            + "strip can live there and cover all ~25 DeliveryFailure construction sites at once. "
            + "Route around it and the strip is still armed and never reached — the payload goes back "
            + "onto the wire whole and the failure report is lost exactly as it was before #3104.");
    }

    /// <summary>
    /// 🚨 ONE seam, not two. A second implementation of <c>Package</c> would be a second way for a
    /// delivery to become its wire form, and the strip would cover only the first — which is
    /// precisely the "a fix landed on one site and missed the other" shape this codebase has already
    /// paid for three times.
    /// </summary>
    [Fact]
    public void ThePackagingSeamHasExactlyOneImplementation()
    {
        var root = SourceScan.FindRepoRoot();
        var declaring = SourceScan.SourceFiles(root, ScannedRoots)
            .Where(f => PackagingSeamDeclaration.IsMatch(SourceScan.MaskCommentsAndStrings(Read(f))))
            .Select(f => SourceScan.Relative(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        // Control arm: the interface AND the implementation must both be seen. Zero would mean the
        // matcher has stopped matching, which reads identically to "there is only one seam".
        Assert.True(
            declaring.Length >= 2,
            "Fewer than two files declare IMessageDelivery Package(...) — expected the interface and "
            + "its one implementation. This guard's matcher has stopped matching, so its uniqueness "
            + "claim is vacuous. Found: [" + string.Join(", ", declaring) + "]");
        Assert.True(
            declaring.Length == 2,
            "More than one implementation of the packaging seam now exists:\n  "
            + string.Join("\n  ", declaring)
            + "\nThe oversized-echo strip is applied in MessageDelivery.Package and covers every "
            + "DeliveryFailure construction site precisely BECAUSE that is the only way a delivery "
            + "becomes its wire form. A second implementation is a second wire path, and a report "
            + "taking it carries the payload it is reporting on. Either route the new one through "
            + "the existing seam, or apply DeliveryPayloadBounds.WithoutOversizedPayload(delivery, "
            + "options) in it and teach this guard about it — in the same change.");
    }

    /// <summary>
    /// 🚨 THE CONTROL ARM FOR THE WHOLE FILE. Every assertion above is about seams that protect
    /// <c>new DeliveryFailure(...)</c> sites. If there are no such sites, the type was renamed or
    /// retired and this guard is reading about something that no longer exists — a wall of green over
    /// an unenforced rule, which is worse than no guard because it reads as evidence.
    /// </summary>
    [Fact]
    public void TheGuardStillHasSubjectsToProtect()
    {
        var root = SourceScan.FindRepoRoot();
        var sites = SourceScan.SourceFiles(root, ScannedRoots)
            .Select(f => (File: SourceScan.Relative(root, f), Count: CountConstructionSitesIn(Read(f))))
            .Where(x => x.Count > 0)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .ToArray();

        var total = sites.Sum(x => x.Count);
        output.WriteLine($"{total} DeliveryFailure construction site(s) across {sites.Length} file(s) "
                         + "— all covered by the two seams asserted in this class:");
        foreach (var (file, count) in sites)
            output.WriteLine($"  {count,3}  {file}");

        Assert.True(
            total > 0,
            "No `new DeliveryFailure(` site was found anywhere under "
            + string.Join(", ", ScannedRoots) + ".\n"
            + "Either the type is gone — in which case delete this guard deliberately and say why in "
            + "the commit — or the scanner is broken, in which case every assertion in this class is "
            + "passing on no evidence. 'Nothing to scan' is a FAILED sweep, never a clean one.");
    }

    /// <summary>
    /// 🚨 NON-VACUITY OF THE MATCHERS, mutation-proved in BOTH directions on planted text.
    ///
    /// <para>Both directions matter equally and are easy to get wrong in opposite ways. A matcher
    /// that fires on nothing scores identically to a clean tree; a matcher that fires on everything
    /// scores identically to a guard that works, right up until someone deletes it for crying wolf.
    /// Planted text is used rather than the live tree so that a spelling stays pinned even after the
    /// tree stops containing an example of it.</para>
    /// </summary>
    [Fact]
    public void TheMatchersSeeWhatTheyClaimToAndNothingElse()
    {
        // ── The packaging seam: strip BEFORE serialize ──────────────────────────────────────────
        Assert.True(StripsBeforeSerializingIn(
            "if (message is DeliveryFailure f)\n"
            + "    message = f with { Delivery = DeliveryPayloadBounds.WithoutOversizedPayload(f.Delivery, o) };\n"
            + "var serialized = JsonSerializer.Serialize(message, o);"));
        // …and the same across lines, which a literal matcher would miss.
        Assert.True(StripsBeforeSerializingIn(
            "message = DeliveryPayloadBounds.WithoutOversizedPayload(d, o);\n"
            + "var s = JsonSerializer\n    .Serialize(message, o);"));

        // The three ways it can be wrong, each of which compiles and passes every functional test.
        Assert.False(StripsBeforeSerializingIn(
            "var serialized = JsonSerializer.Serialize(message, o);"),
            "no strip at all is the pre-#3104 state");
        Assert.False(StripsBeforeSerializingIn(
            "var s = JsonSerializer.Serialize(message, o);\n"
            + "var stripped = DeliveryPayloadBounds.WithoutOversizedPayload(d, o);"),
            "stripping AFTER the serialize strips a report that has already cost the allocation");
        Assert.False(StripsBeforeSerializingIn(
            "// DeliveryPayloadBounds.WithoutOversizedPayload(d, o) used to be called here.\n"
            + "var s = JsonSerializer.Serialize(message, o);"),
            "a commented-out call is not a call — comment masking must hold, or every count here is "
            + "unreliable");
        Assert.False(StripsBeforeSerializingIn(
            "var doc = \"WithoutOversizedPayload before JsonSerializer.Serialize\";"),
            "and neither is a string literal that happens to describe the rule");
        Assert.False(StripsBeforeSerializingIn(
            "message = DeliveryPayloadBounds.WithoutOversizedPayload(d, o);"),
            "no serialize found is a BROKEN SCAN, reported as non-compliant rather than as clean");

        // ── The construction invariant ──────────────────────────────────────────────────────────
        Assert.True(AppliesTheConstructionInvariantIn(
            "public IMessageDelivery Delivery { get; init; } =\n"
            + "    Delivery is null ? null! : DeliveryPayloadBounds.WithoutOversizedPayload(Delivery);"));
        Assert.False(AppliesTheConstructionInvariantIn(
            "public IMessageDelivery Delivery { get; init; } = Delivery;"),
            "the property without the strip is exactly the pre-#3056 state");
        Assert.False(AppliesTheConstructionInvariantIn(
            "/// See DeliveryPayloadBounds.WithoutOversizedPayload for why.\n"
            + "public IMessageDelivery Delivery { get; init; } = Delivery;"),
            "🚨 Events.cs's own remarks name the strip, so a whole-file match would keep passing "
            + "after the invariant was deleted — the exact false pass UntypedContentDegradationGate "
            + "was caught by");

        // ── Routing goes through the seam ───────────────────────────────────────────────────────
        Assert.True(RoutesThroughSeamIn(
            ".DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions));"));
        Assert.True(RoutesThroughSeamIn(
            ".DeliverMessage(\n    delivery\n        .Package(options));"));
        Assert.False(RoutesThroughSeamIn(".DeliverMessage(delivery);"),
            "bypassing the seam leaves the strip armed and unreachable — which is what a merged but "
            + "unreachable guard looks like (#2813)");

        // ── Construction sites (the control arm's matcher) ──────────────────────────────────────
        Assert.Equal(1, CountConstructionSitesIn("Post(new DeliveryFailure(delivery) { });"));
        Assert.Equal(1, CountConstructionSitesIn("Post(new\n    DeliveryFailure(delivery));"));
        Assert.Equal(2, CountConstructionSitesIn(
            "var a = new DeliveryFailure(d); var b = new DeliveryFailure(e, \"x\");"));
        Assert.Equal(0, CountConstructionSitesIn("// new DeliveryFailure(delivery) is the shape."));
        Assert.Equal(0, CountConstructionSitesIn("var s = \"new DeliveryFailure(delivery)\";"));
        Assert.Equal(0, CountConstructionSitesIn("failure with { Delivery = echoed };"));
    }

    internal static bool StripsBeforeSerializingIn(string text)
    {
        var code = SourceScan.MaskCommentsAndStrings(text);
        var serialize = SerializeCall.Match(code);
        if (!serialize.Success)
            return false;
        var strip = code.IndexOf(Strip, StringComparison.Ordinal);
        return strip >= 0 && strip < serialize.Index;
    }

    internal static bool AppliesTheConstructionInvariantIn(string text) =>
        ConstructionInvariant.IsMatch(SourceScan.MaskCommentsAndStrings(text));

    internal static bool RoutesThroughSeamIn(string text) =>
        RoutesThroughSeam.IsMatch(SourceScan.MaskCommentsAndStrings(text));

    internal static int CountConstructionSitesIn(string text) =>
        text.Contains("DeliveryFailure", StringComparison.Ordinal)
            ? ConstructionSite.Matches(SourceScan.MaskCommentsAndStrings(text)).Count
            : 0;

    private static string Read(string absolutePath)
    {
        try { return File.ReadAllText(absolutePath); }
        catch (IOException) { return string.Empty; } // a file a concurrent build is writing is not evidence
    }

    /// <summary>
    /// The masked code of a repo-relative NAMED seam. Both ways of not getting the text FAIL, and
    /// deliberately do not share <see cref="Read"/>'s behaviour.
    ///
    /// <para>🚨 <see cref="Read"/> returns empty on an <see cref="IOException"/> because in a
    /// whole-tree SCAN a file a concurrent build is writing is not evidence of an offence — the
    /// worst case there is one file not counted. Here the file IS the subject: an unreadable seam
    /// would be masked to the empty string, every pattern below would fail to match, and the guard
    /// would report the seam as non-compliant for a reason that has nothing to do with the code. So
    /// each case gets its own message and neither is silently absorbed: a missing file means the
    /// subject MOVED (follow it), an unreadable one means the scan itself failed (re-run it).</para>
    /// </summary>
    private static string Masked(string relative)
    {
        var path = Path.Combine(SourceScan.FindRepoRoot(), relative);
        Assert.True(File.Exists(path),
            $"{relative} is missing — this guard's subject moved; follow it in the same change. An "
            + "assertion about a file that is not there passes having checked nothing.");
        try
        {
            return SourceScan.MaskCommentsAndStrings(File.ReadAllText(path));
        }
        catch (IOException e)
        {
            Assert.Fail(
                $"{relative} could not be read ({e.GetType().Name}: {e.Message}). This is a FAILED "
                + "SCAN, not a non-compliant seam — most likely a concurrent build holding the file. "
                + "Re-run. Do not 'fix' it by treating an unreadable subject as absent: that turns "
                + "the one file this assertion is about into a guaranteed pass or a guaranteed "
                + "failure, and neither says anything about the code.");
            throw; // unreachable — Assert.Fail throws; present so the compiler sees a value on every path
        }
    }
}

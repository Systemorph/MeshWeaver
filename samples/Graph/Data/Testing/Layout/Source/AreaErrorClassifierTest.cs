// <meshweaver>
// Id: Testing/Layout/AreaErrorClassifierTest
// DisplayName: Testing/Layout/AreaErrorClassifierTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using MeshWeaver.Messaging;

/// <summary>
/// Pins the static error classification that drives layout-area wedge-prevention
/// (factored out of NamedAreaView). Each boundary is load-bearing: a transient miss
/// must be retried (bounded), a CompilationInProgress NACK must swap to Progress (not
/// retry), and a permanent miss must fail fast — never an unbounded resubscribe to an
/// inexistent address (the 2026-06-14 portal wedge).
/// </summary>
public class AreaErrorClassifierTest
{
    private static DeliveryFailureException Failure(string? message, ErrorType type, string? nodeTypePath = null)
        // Delivery is unused by the classifier — null! keeps the test dependency-free.
        => new(new DeliveryFailure(null!, message) { ErrorType = type, NodeTypePath = nodeTypePath });

    [MeshTheory]
    [MeshInlineData(typeof(TimeoutException))]
    [MeshInlineData(typeof(OperationCanceledException))]
    public void IsTransientHubFailure_TrueForTimeoutAndCancellation(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        AreaErrorClassifier.IsTransientHubFailure(ex).Should().BeTrue();
    }

    [MeshTheory]
    [MeshInlineData("No response received in hub mesh/x within 30s for request SubscribeRequest")]
    [MeshInlineData("The target hub was not found for address Foo/Bar")]
    [MeshInlineData("Message undeliverable to Foo/Bar")]
    public void IsTransientHubFailure_TrueForFrameworkBanners(string message)
        => AreaErrorClassifier.IsTransientHubFailure(Failure(message, ErrorType.NotFound)).Should().BeTrue();

    [MeshFact]
    public void IsTransientHubFailure_FalseForPermanentError()
        => AreaErrorClassifier.IsTransientHubFailure(new InvalidOperationException("boom")).Should().BeFalse();

    [MeshFact]
    public void TryGetCompilationInProgressNodeType_ReturnsPath()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("compiling", ErrorType.CompilationInProgress, "AgenticPension/Position"))
            .Should().Be("AgenticPension/Position");

    [MeshFact]
    public void TryGetCompilationInProgressNodeType_NullForOtherErrorType()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("nope", ErrorType.NotFound, "AgenticPension/Position"))
            .Should().BeNull();

    [MeshFact]
    public void TryGetCompilationInProgressNodeType_NullWhenNoNodeTypePath()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("compiling", ErrorType.CompilationInProgress))
            .Should().BeNull();

    [MeshTheory]
    [MeshInlineData("Access denied: user lacks Read")]
    [MeshInlineData("No node found at Foo/Bar")]
    [MeshInlineData("Validation failed for X")]
    public void IsExpectedUserActionFailure_TrueForUserOutcomes(string message)
        => AreaErrorClassifier.IsExpectedUserActionFailure(Failure(message, ErrorType.Unauthorized)).Should().BeTrue();

    [MeshFact]
    public void IsExpectedUserActionFailure_TrueForUnauthorizedAccessException()
        => AreaErrorClassifier.IsExpectedUserActionFailure(new UnauthorizedAccessException()).Should().BeTrue();

    [MeshFact]
    public void IsExpectedUserActionFailure_FalseForEngineeringError()
        => AreaErrorClassifier.IsExpectedUserActionFailure(new NullReferenceException()).Should().BeFalse();

    // ── IsNodeGoneNotFound: routing NotFound → render a graceful placeholder, never the raw text ──

    [MeshTheory]
    [MeshInlineData("No node found at Foo/Bar")]
    [MeshInlineData("No node found at 'rbuergi/_Activity/markdown-wXXCCP7IukWc4chwFrhLbw'. Closest ancestor is 'rbuergi' (remainder='_Activity/markdown-wXXCCP7IukWc4chwFrhLbw').")]
    public void IsNodeGoneNotFound_TrueForRoutingNotFound(string message)
        => AreaErrorClassifier.IsNodeGoneNotFound(Failure(message, ErrorType.NotFound)).Should().BeTrue(
            "the raw routing NotFound diagnostic must be caught and replaced with a graceful placeholder, "
            + "not surfaced verbatim to the user (the ephemeral-kernel teardown symptom)");

    [MeshTheory]
    [MeshInlineData("Access denied: user lacks Read")]
    [MeshInlineData("Validation failed for X")]
    public void IsNodeGoneNotFound_FalseForOtherUserActionFailures(string message)
        => AreaErrorClassifier.IsNodeGoneNotFound(Failure(message, ErrorType.Unauthorized)).Should().BeFalse(
            "access-denied / validation carry an actionable message worth showing verbatim — only a "
            + "gone node gets the generic 'no longer available' placeholder");

    [MeshFact]
    public void IsNodeGoneNotFound_FalseForTransientHubNotYetOnline()
        // "target hub was not found" is the transient not-yet-online case (retried), NOT a gone node.
        => AreaErrorClassifier.IsNodeGoneNotFound(
                Failure("The target hub was not found for address Foo/Bar", ErrorType.NotFound)).Should().BeFalse();

    [MeshFact]
    public void IsNodeGoneNotFound_FalseForNonDeliveryFailure()
        => AreaErrorClassifier.IsNodeGoneNotFound(new InvalidOperationException("No node found at X")).Should().BeFalse(
            "only a routing DeliveryFailure counts — a coincidental message on some other exception must not");

    // ── TryGetMissingNodePath: name the broken reference, never the routing internals (#1456) ──

    /// <summary>
    /// The path is the one part of the diagnostic an author can act on. "Closest ancestor" and
    /// "remainder=" are routing internals — the extractor must hand back the path ALONE so a view
    /// can name it without reproducing the framework string around it.
    /// </summary>
    [MeshFact]
    public void TryGetMissingNodePath_ReturnsThePathWithoutTheRoutingTail()
        => AreaErrorClassifier.TryGetMissingNodePath(Failure(
                "No node found at 'ClientDelta/Abschlusspraesentation/04-extraktion'. Closest ancestor is "
                + "'ClientDelta/Abschlusspraesentation' (remainder='04-extraktion').", ErrorType.NotFound))
            .Should().Be("ClientDelta/Abschlusspraesentation/04-extraktion");

    [MeshFact]
    public void TryGetMissingNodePath_NullWhenTheMessageCarriesNoQuotedPath()
        => AreaErrorClassifier.TryGetMissingNodePath(
            Failure("No node found at Foo/Bar", ErrorType.NotFound)).Should().BeNull(
            "an unquoted form gives nothing safe to show — better to omit the path than to guess at it");

    [MeshFact]
    public void TryGetMissingNodePath_NullForOtherFailures()
    {
        AreaErrorClassifier.TryGetMissingNodePath(
            Failure("Access denied: user lacks Read on 'X/Y'", ErrorType.Unauthorized)).Should().BeNull();
        AreaErrorClassifier.TryGetMissingNodePath(
            new InvalidOperationException("No node found at 'X/Y'.")).Should().BeNull(
            "only a routing DeliveryFailure counts, exactly as for IsNodeGoneNotFound");
        AreaErrorClassifier.TryGetMissingNodePath(null).Should().BeNull();
    }

    // ── TryGetInitializationFailureReason: a FAILED hub → render the reason, terminal, no retry (#323) ──

    [MeshTheory]
    // The first request that triggered init (HandleInitialize's .Catch — em-dash form):
    [MeshInlineData("Hub 'AgenticPension/x' initialization failed — a BuildupAction faulted (InvalidOperationException: seed missing)")]
    // Every later request (EnterInitializationFailedState's refusal rule — colon form):
    [MeshInlineData("Hub 'AgenticPension/x' initialization failed: seed missing")]
    public void TryGetInitializationFailureReason_ReturnsReasonForBothBanners(string message)
        => AreaErrorClassifier.TryGetInitializationFailureReason(Failure(message, ErrorType.Failed))
            .Should().Be(message,
                "a hub that failed initialization carries the real reason in its DeliveryFailure — the "
                + "client must surface it, not the generic 'did not become addressable' banner");

    [MeshFact]
    public void TryGetInitializationFailureReason_NullForCompilationInProgress()
        // A CompilationInProgress NACK is a DISTINCT, transient case (swap to Progress) — not an init failure.
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                Failure("compiling", ErrorType.CompilationInProgress, "Foo/Bar")).Should().BeNull();

    [MeshFact]
    public void TryGetInitializationFailureReason_NullForOtherDeliveryFailure()
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                Failure("The target hub was not found for address Foo/Bar", ErrorType.NotFound)).Should().BeNull();

    [MeshFact]
    public void TryGetInitializationFailureReason_NullForNonDeliveryFailure()
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                new InvalidOperationException("Hub 'x' initialization failed: boom")).Should().BeNull(
            "only a typed DeliveryFailure counts — a coincidental message on some other exception must not");

    [MeshFact]
    public void InitializationFailure_ClassifiesAsTerminalWithReason_NotTransient()
    {
        // The init-failure banner must NOT be mistaken for a transient (retried) miss — otherwise the
        // client would spin against a durably-failed hub instead of showing the reason once.
        var initFailure = Failure("Hub 'x' initialization failed: seed missing", ErrorType.Failed);
        AreaErrorClassifier.TryGetInitializationFailureReason(initFailure).Should().NotBeNull();
        AreaErrorClassifier.IsTransientHubFailure(initFailure).Should().BeFalse();
    }

    // ── ShouldRetryArea: the single predicate the subscription hands the retry operator ──

    [MeshFact]
    public void ShouldRetryArea_TrueForTransientMiss()
        => AreaErrorClassifier.ShouldRetryArea(new TimeoutException("No response received in hub")).Should().BeTrue();

    [MeshFact]
    public void ShouldRetryArea_FalseForCompilationInProgress_HandledImmediatelyNotRetried()
        => AreaErrorClassifier.ShouldRetryArea(
                Failure("compiling", ErrorType.CompilationInProgress, "Foo/Bar")).Should().BeFalse();

    [MeshFact]
    public void ShouldRetryArea_FalseForDisposal_BenignTeardown()
        => AreaErrorClassifier.ShouldRetryArea(new ObjectDisposedException("circuit")).Should().BeFalse();

    [MeshFact]
    public void ShouldRetryArea_FalseForInitializationFailure_TerminalNotRetried()
        // A durably-failed hub won't recover on resubscribe — render the reason once, never spin (#323).
        => AreaErrorClassifier.ShouldRetryArea(
                Failure("Hub 'x' initialization failed: seed missing", ErrorType.Failed)).Should().BeFalse();

    [MeshFact]
    public void ShouldRetryArea_FalseForPermanentError()
        => AreaErrorClassifier.ShouldRetryArea(new InvalidOperationException("boom")).Should().BeFalse();

    // ── IsRoutingNotFoundFailure: raw-message twin the portal error sink uses to swallow a benign
    //    routing NotFound (a not-yet-run per-viewer Activity area) from the global "Something went wrong" modal ──

    [MeshTheory]
    [MeshInlineData("No node found at 'u/_Activity/markdown-abc'. Closest ancestor is 'u' (remainder='_Activity/markdown-abc').")]
    [MeshInlineData("No node found at 'x/y'.")]
    public void IsRoutingNotFoundFailure_TrueForRoutingNotFound(string message)
        => AreaErrorClassifier.IsRoutingNotFoundFailure(
                new DeliveryFailure(null!, message) { ErrorType = ErrorType.NotFound }).Should().BeTrue(
            "a routing NotFound for a gone / not-yet-created node is benign churn — swallowed, never the modal");

    [MeshFact]
    public void IsRoutingNotFoundFailure_FalseForNotFoundWithoutBanner()
        => AreaErrorClassifier.IsRoutingNotFoundFailure(
                new DeliveryFailure(null!, "access denied") { ErrorType = ErrorType.NotFound }).Should().BeFalse();

    [MeshFact]
    public void IsRoutingNotFoundFailure_FalseForOtherErrorTypeWithBanner()
        // A genuine failure that happens to start "No node found" but isn't a routing NotFound stays visible.
        => AreaErrorClassifier.IsRoutingNotFoundFailure(
                new DeliveryFailure(null!, "No node found") { ErrorType = ErrorType.Failed }).Should().BeFalse();

    [MeshFact]
    public void IsRoutingNotFoundFailure_FalseForNull()
        => AreaErrorClassifier.IsRoutingNotFoundFailure(null).Should().BeFalse();

    // ── TryGetAccessDeniedPath / TryGetPaywallPath: the "no access ⇒ paywall page" fallback. A
    //    not-yet-enrolled visitor who hits a gated course is redirected to its PUBLIC paywall instead
    //    of a raw "Access denied" card — so the discovery funnel never dead-ends on an error. ──

    [MeshTheory]
    [MeshInlineData("Access denied: user 'roland.buergi' lacks Read permission on 'AgenticEngineering'", "AgenticEngineering")]
    [MeshInlineData("Access denied: user 'u' lacks Read permission on 'AgenticEngineering/Module1'", "AgenticEngineering/Module1")]
    [MeshInlineData("User 'u' lacks Read permission on 'DataModeling'", "DataModeling")]
    public void TryGetAccessDeniedPath_ExtractsThePath(string message, string expected)
    {
        AreaErrorClassifier.TryGetAccessDeniedPath(Failure(message, ErrorType.Unauthorized))
            .Should().Be(expected);
        // Works on a plain UnauthorizedAccessException too (same banner, no DeliveryFailure wrapper).
        AreaErrorClassifier.TryGetAccessDeniedPath(new UnauthorizedAccessException(message))
            .Should().Be(expected);
    }

    [MeshTheory]
    [MeshInlineData("No node found at 'x/y'.")]                       // routing NotFound — different banner
    [MeshInlineData("Validation failed for field 'name'")]            // no "permission on '"
    [MeshInlineData("boom")]
    public void TryGetAccessDeniedPath_NullWhenNoQuotedPath(string message)
        => AreaErrorClassifier.TryGetAccessDeniedPath(Failure(message, ErrorType.Failed)).Should().BeNull();

    [MeshFact]
    public void TryGetAccessDeniedPath_NullForNull()
        => AreaErrorClassifier.TryGetAccessDeniedPath(null).Should().BeNull();

    // ── IsSafeRedirect: the loop-guard for the "no access ⇒ redirect here" feature. The redirect
    //    TARGET comes from the node's PartitionAccessPolicy (hub.GetRedirectOnDenied); this only decides
    //    whether sending the denied node THERE can loop. ──

    [MeshTheory]
    // A gated node redirects to a DIFFERENT public page → safe.
    [MeshInlineData("AgenticEngineering/Introduction", "AgenticEngineering/Cover", true)]
    // A leading '/' on the target is ignored.
    [MeshInlineData("AgenticEngineering/Introduction", "/AgenticEngineering/Cover", true)]
    // The redirect TARGET itself being denied → would loop → NOT safe.
    [MeshInlineData("AgenticEngineering/Cover", "AgenticEngineering/Cover", false)]
    // A node UNDER the target being denied → would loop → NOT safe.
    [MeshInlineData("AgenticEngineering/Cover/Sub", "AgenticEngineering/Cover", false)]
    // No target configured → nothing to redirect to.
    [MeshInlineData("AgenticEngineering/Introduction", null, false)]
    [MeshInlineData("AgenticEngineering/Introduction", "", false)]
    [MeshInlineData("AgenticEngineering/Introduction", "   ", false)]
    // No denied path → nothing to redirect.
    [MeshInlineData(null, "AgenticEngineering/Cover", false)]
    public void IsSafeRedirect_LoopGuard(string? deniedPath, string? redirectPath, bool expected)
        => AreaErrorClassifier.IsSafeRedirect(deniedPath, redirectPath).Should().Be(expected);

    // ── NO VERDICT (issue #974). ErrorType.Unavailable means the mesh could not decide —
    //    neither "you may not" nor "it isn't there". The GUI must render the honest
    //    "temporarily unavailable" copy for it, never an access-denied card. ──

    [MeshFact]
    public void IsAvailabilityFailure_TrueForTheUnavailableErrorType()
        => AreaErrorClassifier.IsAvailabilityFailure(
                Failure("Permission check unavailable for user 'x' on 'y' — no verdict was reached",
                    ErrorType.Unavailable))
            .Should().BeTrue();

    [MeshFact]
    public void IsAvailabilityFailure_MatchesNestedFailures()
        => AreaErrorClassifier.IsAvailabilityFailure(
                new InvalidOperationException("wrapped", Failure("no verdict", ErrorType.Unavailable)))
            .Should().BeTrue();

    [MeshTheory]
    [MeshInlineData(ErrorType.Unauthorized)]
    [MeshInlineData(ErrorType.Forbidden)]
    [MeshInlineData(ErrorType.NotFound)]
    [MeshInlineData(ErrorType.Failed)]
    public void IsAvailabilityFailure_FalseForEveryDecidedOutcome(ErrorType type)
        // A real verdict — in either direction — is not an availability failure. Without this the
        // fix would swallow genuine denials and 404s into a "retry shortly" card.
        => AreaErrorClassifier.IsAvailabilityFailure(Failure("decided", type)).Should().BeFalse();

    [MeshFact]
    public void IsAvailabilityFailure_FalseForAPlainException()
        => AreaErrorClassifier.IsAvailabilityFailure(new InvalidOperationException("boom")).Should().BeFalse();

    [MeshFact]
    public void AnUnavailableFailure_IsNotAUserActionFailure_EvenThoughItSaysPermission()
    {
        // 🚨 The trap this short-circuit exists for. The honest message is "Permission check
        // unavailable …", and IsExpectedUserActionFailure matches the bare substring "permission".
        // Without the typed check winning first, an infrastructure fault would be filed as "user
        // clicked a thing they couldn't do" and logged at Warning — exactly the wrong level for
        // the one condition an operator most needs to see.
        var unavailable = Failure(
            "Permission check unavailable for user 'x' on 'y' (Read) — no verdict was reached",
            ErrorType.Unavailable);

        AreaErrorClassifier.IsExpectedUserActionFailure(unavailable).Should().BeFalse();
    }

    [MeshFact]
    public void AnUnavailableFailure_IsNotMistakenForAGoneNode()
        // "no verdict" must not route into the "this view is no longer available" placeholder,
        // which tells the user the thing is gone.
        => AreaErrorClassifier.IsNodeGoneNotFound(
                Failure("Permission check unavailable — no verdict was reached", ErrorType.Unavailable))
            .Should().BeFalse();

    [MeshFact]
    public void AnUnavailableFailure_CarriesNoAccessDeniedPath_SoItCannotTriggerAPaywallRedirect()
        // TryGetAccessDeniedPath drives "no access ⇒ redirect to the public cover". Firing that on
        // a fold that never ran would bounce an entitled viewer to a sign-up page.
        => AreaErrorClassifier.TryGetAccessDeniedPath(
                Failure("Permission check unavailable for user 'x' on 'AgenticEngineering/Intro' (Read)",
                    ErrorType.Unavailable))
            .Should().BeNull();

    // ── IsAccessDenied: the server-side render pipeline swaps the raw exception text for the
    //    standard localized access-denied presentation on a DEFINITE denial (issue #1182) ──

    [MeshFact]
    public void IsAccessDenied_TrueForTypedUnauthorizedAccessException()
        // The exact prod shape: the access layer throws UnauthorizedAccessException
        // ("User 'carson' lacks Read permission on 'Profiles/RolandLinkedIn'") mid-render.
        => AreaErrorClassifier.IsAccessDenied(
                new UnauthorizedAccessException("User 'carson' lacks Read permission on 'Profiles/RolandLinkedIn'"))
            .Should().BeTrue();

    [MeshFact]
    public void IsAccessDenied_TrueWhenTheDenialIsWrapped()
        => AreaErrorClassifier.IsAccessDenied(
                new InvalidOperationException("render faulted",
                    new UnauthorizedAccessException("User 'x' lacks Read permission on 'y'")))
            .Should().BeTrue();

    [MeshFact]
    public void IsAccessDenied_TrueForTheAccessDeniedBanner()
        // The delivery-failure banner carries no typed exception; the quoted-path banner is
        // the same signal TryGetAccessDeniedPath keys the paywall redirect on.
        => AreaErrorClassifier.IsAccessDenied(
                Failure("Access denied: user 'x' lacks Read permission on 'AgenticEngineering'",
                    ErrorType.Unauthorized))
            .Should().BeTrue();

    [MeshFact]
    public void IsAccessDenied_FalseForAnEngineeringError()
        // A crash must keep the generic error panel carrying the exception message —
        // presenting it as "Access denied" would send the user to ask for rights they hold.
        => AreaErrorClassifier.IsAccessDenied(new NullReferenceException("boom")).Should().BeFalse();

    [MeshFact]
    public void IsAccessDenied_FalseForAnAvailabilityFailure()
        // NO VERDICT (issue #974): nothing was decided about the caller's rights, so it must
        // never be presented as a denial — the typed check wins over any message wording.
        => AreaErrorClassifier.IsAccessDenied(
                Failure("Permission check unavailable for user 'x' on 'y' (Read) — no verdict was reached",
                    ErrorType.Unavailable))
            .Should().BeFalse();

    [MeshFact]
    public void IsAccessDenied_FalseForAValidationRejection()
        // Validation is an expected USER-ACTION failure (Warning-level) but NOT a denial —
        // its message is actionable and must stay visible verbatim.
        => AreaErrorClassifier.IsAccessDenied(Failure("Validation failed for X", ErrorType.Failed))
            .Should().BeFalse();
}

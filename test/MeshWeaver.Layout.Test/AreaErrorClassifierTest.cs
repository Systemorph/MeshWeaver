using System;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

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

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(OperationCanceledException))]
    public void IsTransientHubFailure_TrueForTimeoutAndCancellation(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        AreaErrorClassifier.IsTransientHubFailure(ex).Should().BeTrue();
    }

    [Theory]
    [InlineData("No response received in hub mesh/x within 30s for request SubscribeRequest")]
    [InlineData("The target hub was not found for address Foo/Bar")]
    [InlineData("Message undeliverable to Foo/Bar")]
    public void IsTransientHubFailure_TrueForFrameworkBanners(string message)
        => AreaErrorClassifier.IsTransientHubFailure(Failure(message, ErrorType.NotFound)).Should().BeTrue();

    [Fact]
    public void IsTransientHubFailure_FalseForPermanentError()
        => AreaErrorClassifier.IsTransientHubFailure(new InvalidOperationException("boom")).Should().BeFalse();

    [Fact]
    public void TryGetCompilationInProgressNodeType_ReturnsPath()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("compiling", ErrorType.CompilationInProgress, "AgenticPension/Position"))
            .Should().Be("AgenticPension/Position");

    [Fact]
    public void TryGetCompilationInProgressNodeType_NullForOtherErrorType()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("nope", ErrorType.NotFound, "AgenticPension/Position"))
            .Should().BeNull();

    [Fact]
    public void TryGetCompilationInProgressNodeType_NullWhenNoNodeTypePath()
        => AreaErrorClassifier.TryGetCompilationInProgressNodeType(
                Failure("compiling", ErrorType.CompilationInProgress))
            .Should().BeNull();

    [Theory]
    [InlineData("Access denied: user lacks Read")]
    [InlineData("No node found at Foo/Bar")]
    [InlineData("Validation failed for X")]
    public void IsExpectedUserActionFailure_TrueForUserOutcomes(string message)
        => AreaErrorClassifier.IsExpectedUserActionFailure(Failure(message, ErrorType.Unauthorized)).Should().BeTrue();

    [Fact]
    public void IsExpectedUserActionFailure_TrueForUnauthorizedAccessException()
        => AreaErrorClassifier.IsExpectedUserActionFailure(new UnauthorizedAccessException()).Should().BeTrue();

    [Fact]
    public void IsExpectedUserActionFailure_FalseForEngineeringError()
        => AreaErrorClassifier.IsExpectedUserActionFailure(new NullReferenceException()).Should().BeFalse();

    // ── IsNodeGoneNotFound: routing NotFound → render a graceful placeholder, never the raw text ──

    [Theory]
    [InlineData("No node found at Foo/Bar")]
    [InlineData("No node found at 'rbuergi/_Activity/markdown-wXXCCP7IukWc4chwFrhLbw'. Closest ancestor is 'rbuergi' (remainder='_Activity/markdown-wXXCCP7IukWc4chwFrhLbw').")]
    public void IsNodeGoneNotFound_TrueForRoutingNotFound(string message)
        => AreaErrorClassifier.IsNodeGoneNotFound(Failure(message, ErrorType.NotFound)).Should().BeTrue(
            "the raw routing NotFound diagnostic must be caught and replaced with a graceful placeholder, "
            + "not surfaced verbatim to the user (the ephemeral-kernel teardown symptom)");

    [Theory]
    [InlineData("Access denied: user lacks Read")]
    [InlineData("Validation failed for X")]
    public void IsNodeGoneNotFound_FalseForOtherUserActionFailures(string message)
        => AreaErrorClassifier.IsNodeGoneNotFound(Failure(message, ErrorType.Unauthorized)).Should().BeFalse(
            "access-denied / validation carry an actionable message worth showing verbatim — only a "
            + "gone node gets the generic 'no longer available' placeholder");

    [Fact]
    public void IsNodeGoneNotFound_FalseForTransientHubNotYetOnline()
        // "target hub was not found" is the transient not-yet-online case (retried), NOT a gone node.
        => AreaErrorClassifier.IsNodeGoneNotFound(
                Failure("The target hub was not found for address Foo/Bar", ErrorType.NotFound)).Should().BeFalse();

    [Fact]
    public void IsNodeGoneNotFound_FalseForNonDeliveryFailure()
        => AreaErrorClassifier.IsNodeGoneNotFound(new InvalidOperationException("No node found at X")).Should().BeFalse(
            "only a routing DeliveryFailure counts — a coincidental message on some other exception must not");

    // ── TryGetInitializationFailureReason: a FAILED hub → render the reason, terminal, no retry (#323) ──

    [Theory]
    // The first request that triggered init (HandleInitialize's .Catch — em-dash form):
    [InlineData("Hub 'AgenticPension/x' initialization failed — a BuildupAction faulted (InvalidOperationException: seed missing)")]
    // Every later request (EnterInitializationFailedState's refusal rule — colon form):
    [InlineData("Hub 'AgenticPension/x' initialization failed: seed missing")]
    public void TryGetInitializationFailureReason_ReturnsReasonForBothBanners(string message)
        => AreaErrorClassifier.TryGetInitializationFailureReason(Failure(message, ErrorType.Failed))
            .Should().Be(message,
                "a hub that failed initialization carries the real reason in its DeliveryFailure — the "
                + "client must surface it, not the generic 'did not become addressable' banner");

    [Fact]
    public void TryGetInitializationFailureReason_NullForCompilationInProgress()
        // A CompilationInProgress NACK is a DISTINCT, transient case (swap to Progress) — not an init failure.
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                Failure("compiling", ErrorType.CompilationInProgress, "Foo/Bar")).Should().BeNull();

    [Fact]
    public void TryGetInitializationFailureReason_NullForOtherDeliveryFailure()
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                Failure("The target hub was not found for address Foo/Bar", ErrorType.NotFound)).Should().BeNull();

    [Fact]
    public void TryGetInitializationFailureReason_NullForNonDeliveryFailure()
        => AreaErrorClassifier.TryGetInitializationFailureReason(
                new InvalidOperationException("Hub 'x' initialization failed: boom")).Should().BeNull(
            "only a typed DeliveryFailure counts — a coincidental message on some other exception must not");

    [Fact]
    public void InitializationFailure_ClassifiesAsTerminalWithReason_NotTransient()
    {
        // The init-failure banner must NOT be mistaken for a transient (retried) miss — otherwise the
        // client would spin against a durably-failed hub instead of showing the reason once.
        var initFailure = Failure("Hub 'x' initialization failed: seed missing", ErrorType.Failed);
        AreaErrorClassifier.TryGetInitializationFailureReason(initFailure).Should().NotBeNull();
        AreaErrorClassifier.IsTransientHubFailure(initFailure).Should().BeFalse();
    }

    // ── ShouldRetryArea: the single predicate the subscription hands the retry operator ──

    [Fact]
    public void ShouldRetryArea_TrueForTransientMiss()
        => AreaErrorClassifier.ShouldRetryArea(new TimeoutException("No response received in hub")).Should().BeTrue();

    [Fact]
    public void ShouldRetryArea_FalseForCompilationInProgress_HandledImmediatelyNotRetried()
        => AreaErrorClassifier.ShouldRetryArea(
                Failure("compiling", ErrorType.CompilationInProgress, "Foo/Bar")).Should().BeFalse();

    [Fact]
    public void ShouldRetryArea_FalseForDisposal_BenignTeardown()
        => AreaErrorClassifier.ShouldRetryArea(new ObjectDisposedException("circuit")).Should().BeFalse();

    [Fact]
    public void ShouldRetryArea_FalseForInitializationFailure_TerminalNotRetried()
        // A durably-failed hub won't recover on resubscribe — render the reason once, never spin (#323).
        => AreaErrorClassifier.ShouldRetryArea(
                Failure("Hub 'x' initialization failed: seed missing", ErrorType.Failed)).Should().BeFalse();

    [Fact]
    public void ShouldRetryArea_FalseForPermanentError()
        => AreaErrorClassifier.ShouldRetryArea(new InvalidOperationException("boom")).Should().BeFalse();
}

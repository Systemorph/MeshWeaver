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
    public void ShouldRetryArea_FalseForPermanentError()
        => AreaErrorClassifier.ShouldRetryArea(new InvalidOperationException("boom")).Should().BeFalse();
}

using System;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 THE DISPOSITION OF A FAULTED CONTENT READ, pinned arm by arm.
///
/// <para>This exists because the mapping has ALREADY been proposed in a wrong shape. A security fix
/// under review collapsed <i>every</i> <see cref="DeliveryFailureException"/> to <c>404</c> —
/// correct for a refusal, and a correctness regression for the other arm: it turns "the permission
/// gate could not reach a verdict" into a confident, cacheable "this does not exist". Issue #974
/// went to some trouble to keep those two facts apart all the way down the access pipeline, and a
/// single <c>ex is DeliveryFailureException ? NotFound</c> at the HTTP boundary throws that away at
/// the last possible moment. Nothing else in the suite would have noticed.</para>
///
/// <para>These run against the mapping function directly rather than over HTTP, because the second
/// and third arms are not deterministically reachable from a request: they need the owning hub to
/// fail in a specific classified way. The route-level behaviour — that a REFUSED read answers 404
/// and says nothing about itself — is pinned end-to-end over real HTTP in
/// <c>StaticContentUnmountedTest</c>; this file pins the classification those tests ride on.</para>
///
/// <para>The <see cref="DeliveryFailure"/> records are real, not mocks. Their <c>Delivery</c> is
/// left null deliberately: the mapping reads <c>ErrorType</c> and the exception message and nothing
/// else, so binding a live delivery would add a whole mesh to the fixture without adding a single
/// assertion.</para>
/// </summary>
public class ContentFailureMappingTest
{
    private const string SensitiveDetail =
        "Access denied: user 'Anonymous' lacks Read permission on 'PrivateSpace'";

    private static DeliveryFailureException Failure(ErrorType errorType, string message) =>
        new(new DeliveryFailure(null!, message) { ErrorType = errorType });

    /// <summary>
    /// A refusal answers exactly like a miss — 404 — so the status cannot be used as an existence
    /// oracle over a fully predictable URL scheme.
    /// </summary>
    [Fact]
    public void ARefusedRead_AnswersAsAMissingOne()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(ErrorType.Unauthorized, SensitiveDetail), logger: null, path: "PrivateSpace/secret.pdf");

        result.Should().BeOfType<NotFound<string>>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// 🚨 THE ARM THAT MUST NOT COLLAPSE INTO THE ONE ABOVE. "We could not check" is not "you may
    /// not" and is not "it is not there": no verdict was reached, so the honest answer is a
    /// retryable 503. Serving 404 here would let a caller — or a CDN — cache an absence that was
    /// never asserted, and would hide a degraded permission backend behind a perfectly ordinary
    /// looking "not found".
    /// </summary>
    [Fact]
    public void AnUndeterminedVerdict_Refuses_ButAsRetryable_NotAsAbsence()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(ErrorType.Unavailable, "Permission check unavailable — no verdict was reached"),
            logger: null, path: "PrivateSpace/secret.pdf");

        result.Should().BeOfType<StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
                "a permission probe that reached no verdict is a degraded dependency, not a statement " +
                "that the file is absent — collapsing it to 404 is the regression this test exists for");
    }

    /// <summary>
    /// 🚨 A HUB THAT IS RECYCLING IS NOT A BROKEN ONE. <see cref="ErrorType.ShuttingDown"/>'s own
    /// contract is "retry-worthy, never terminal" — routing mints it deliberately INSTEAD of
    /// NotFound for a live-but-recycling address. Answering 500 (or 404) for a hub that will be back
    /// on the next probe is the confident-wrong-answer the tri-state exists to prevent, so it joins
    /// the retryable arm rather than the alerting fallback.
    /// </summary>
    [Fact]
    public void ARecyclingHub_IsRetryable_NotAFailure()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(ErrorType.ShuttingDown, "Hub 'PrivateSpace' is shutting down. Rejecting now."),
            logger: null, path: "PrivateSpace/secret.pdf");

        result.Should().BeOfType<StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// 🚨 THE #1563 / #1748 ARM. A read that gave up because the owning hub never answered reached
    /// no verdict either — same fact as <see cref="ErrorType.Unavailable"/>, same retryable 503.
    /// Before the route carried a budget of its own this arrived only after the hub's full 60 s
    /// <c>RequestTimeout</c> and fell through to the 500 + <c>fail:</c> fallback, which is how a
    /// transient miss on one image became a filed production incident.
    /// </summary>
    [Fact]
    public void AReadThatTimedOut_IsRetryable_NotAFailure()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            new HubUnreachableException(
                "Reading content collection config from 'Skill' gave up after 10s",
                target: "Skill", budget: TimeSpan.FromSeconds(10)),
            logger: null, path: "Skill/content/og-card.png");

        result.Should().BeOfType<StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
                "the hub never answered — that is an availability fact, and the same request "
                + "succeeds once the target is back");
    }

    /// <summary>
    /// A plain <see cref="TimeoutException"/> takes the same arm: the classification must key on the
    /// FACT (a read that reached no verdict), not on this repo's own exception subclass, or a
    /// timeout arriving from any other layer would quietly rejoin the alerting fallback.
    /// </summary>
    [Fact]
    public void AnyTimeout_TakesTheSameRetryableArm()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            new TimeoutException("No response received in hub mesh/x within 00:01:00"),
            logger: null, path: "Skill/content/og-card.png");

        result.Should().BeOfType<StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// 🚨 ROUTING SAID THERE IS NO NODE — so answer as a miss, immediately, and with the SAME body a
    /// refusal produces. This used to fall through to the 500 + Error fallback, which alerted on
    /// every request for a node that had simply been deleted, AND made the response distinguishable
    /// from a refusal: 500-vs-404 over a fully predictable URL scheme is an existence oracle in the
    /// other direction. Both go away by putting absence on the arm that already means absence.
    /// </summary>
    [Fact]
    public void ARoutingNotFound_AnswersAsAMissingFile_NotAsAServerFault()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(ErrorType.NotFound, "No node found at 'PrivateSpace'."),
            logger: null, path: "PrivateSpace/secret.pdf");

        var notFound = result.Should().BeOfType<NotFound<string>>().Which;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        notFound.Value.Should().NotContain("PrivateSpace",
            "the body stays the constant a refusal uses, so the two are byte-identical");
    }

    /// <summary>
    /// Fail closed either way: neither refusal arm serves anything, and neither is a 2xx.
    /// </summary>
    [Theory]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Unavailable)]
    [InlineData(ErrorType.ShuttingDown)]
    [InlineData(ErrorType.NotFound)]
    public void NeitherRefusalArm_EverServes(ErrorType errorType)
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(errorType, SensitiveDetail), logger: null, path: "PrivateSpace/secret.pdf");

        var status = result switch
        {
            IStatusCodeHttpResult { StatusCode: { } code } => code,
            _ => 0
        };
        status.Should().BeGreaterThanOrEqualTo(400, "a faulted read must never answer with success");
    }

    /// <summary>
    /// 🚨 NO EXCEPTION TEXT IN THE BODY, on the arm that used to carry it. An unclassified failure
    /// still arrives holding the refusing hub's own diagnostic message — and the fallback arm is
    /// where every failure a hub reports WITHOUT classifying lands, including
    /// <see cref="ErrorType.Unknown"/>, <see cref="ErrorType.Exception"/> and a
    /// <see cref="DeliveryFailureException"/> constructed with no <c>Failure</c> at all (the
    /// property patterns simply do not match those). Echoing it published the permission model, the
    /// principal and the node's existence to an anonymous caller over a public route.
    /// </summary>
    [Theory]
    [InlineData(ErrorType.Unknown)]
    [InlineData(ErrorType.Exception)]
    [InlineData(ErrorType.Failed)]
    public void AnUnclassifiedFailure_NeverEchoesTheExceptionText(ErrorType errorType)
    {
        var result = BlazorHostingExtensions.ContentFailure(
            Failure(errorType, SensitiveDetail), logger: null, path: "PrivateSpace/secret.pdf");

        var problem = result.Should().BeOfType<ProblemHttpResult>().Which;
        problem.ProblemDetails.Detail.Should().NotContain("Access denied");
        problem.ProblemDetails.Detail.Should().NotContain("PrivateSpace");
        problem.ProblemDetails.Detail.Should().NotContain("Permission");
    }

    /// <summary>
    /// The same suppression for a plain exception — the fallback must not become message-echoing
    /// again simply because the fault did not arrive as a delivery failure.
    /// </summary>
    [Fact]
    public void APlainException_NeverEchoesItsMessageEither()
    {
        var result = BlazorHostingExtensions.ContentFailure(
            new InvalidOperationException(SensitiveDetail), logger: null, path: "PrivateSpace/secret.pdf");

        result.Should().BeOfType<ProblemHttpResult>()
            .Which.ProblemDetails.Detail.Should().NotContain("Access denied");
    }
}

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
    /// Fail closed either way: neither refusal arm serves anything, and neither is a 2xx.
    /// </summary>
    [Theory]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Unavailable)]
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
    /// <see cref="ErrorType.Unknown"/>, <see cref="ErrorType.NotFound"/> and a
    /// <see cref="DeliveryFailureException"/> constructed with no <c>Failure</c> at all (the
    /// property patterns simply do not match those). Echoing it published the permission model, the
    /// principal and the node's existence to an anonymous caller over a public route.
    /// </summary>
    [Theory]
    [InlineData(ErrorType.Unknown)]
    [InlineData(ErrorType.NotFound)]
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

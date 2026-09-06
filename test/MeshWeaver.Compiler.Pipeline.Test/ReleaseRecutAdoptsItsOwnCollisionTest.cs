using System;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A release re-cut that collides with its OWN first attempt has succeeded, not failed.
///
/// <para><c>ReleasePostCondition</c> re-cuts when <c>latestReleasePath</c> still names an earlier
/// build. The id it mints is <c>{yyyyMMddHHmmss}-{8 chars of SHA256(Collection/ContentPath)}</c>, so
/// when the retry lands in the same SECOND as the first attempt both mint the same id and the second
/// create is rejected. <c>TryCreateReleaseNode</c> caught every exception into <c>null</c>, so the
/// pointer never advanced: the bytes were published and the Release node existed, while the NodeType
/// went on advertising a build no release named and every instance kept executing the previous
/// assembly (#3407, measured on <c>Edu/CourseInvite</c> build 767 — only a pod restart cleared it).</para>
///
/// <para><b>Adopting is naming the same bytes, not guessing.</b> The hash half of the id comes from
/// the durable content reference, so an equal id means equal second AND equal content — a collision
/// can only be this same code's own earlier attempt for this same compile.</para>
/// </summary>
public class ReleaseRecutAdoptsItsOwnCollisionTest
{
    private const string ReleasePath = "Edu/CourseInvite/Release/20260906094912-EsO3j1xI";

    /// <summary>The case #3407 is: the path comes back so <c>latestReleasePath</c> can advance.</summary>
    [Fact]
    public void AnAlreadyExistsFailure_AdoptsTheReleasePath()
        => Assert.Equal(
            ReleasePath,
            NodeTypeBuildState.AdoptOnOwnCollision(
                CreateNodeResponse.Fail("taken", NodeCreationRejectionReason.NodeAlreadyExists)
                    .ToException(ReleasePath),
                ReleasePath));

    /// <summary>Both production wordings, for a producer that throws directly rather than through
    /// <c>ToException</c> — the classification must not depend on which one it met.</summary>
    [Theory]
    [InlineData("Node already exists: Edu/CourseInvite/Release/20260906094912-EsO3j1xI")]
    [InlineData("Node already exists at path: Edu/CourseInvite/Release/20260906094912-EsO3j1xI")]
    public void EitherWording_Adopts(string message)
        => Assert.Equal(
            ReleasePath,
            NodeTypeBuildState.AdoptOnOwnCollision(new InvalidOperationException(message), ReleasePath));

    /// <summary>
    /// 🚨 The other half, and the reason this is not simply "swallow the error". A create that
    /// failed for ANY other reason must still leave the pointer un-advanced — advertising a release
    /// path whose node does not exist would be worse than the bug being fixed.
    /// </summary>
    [Theory]
    [InlineData(NodeCreationRejectionReason.ValidationFailed)]
    [InlineData(NodeCreationRejectionReason.InvalidNodeType)]
    [InlineData(NodeCreationRejectionReason.InvalidPath)]
    [InlineData(NodeCreationRejectionReason.Unknown)]
    public void EveryOtherFailure_StillDoesNotAdopt(NodeCreationRejectionReason reason)
        => Assert.Null(NodeTypeBuildState.AdoptOnOwnCollision(
            CreateNodeResponse.Fail("nope", reason).ToException(ReleasePath),
            ReleasePath));

    [Fact]
    public void ATimeoutOrTransportFault_StillDoesNotAdopt()
        => Assert.Null(NodeTypeBuildState.AdoptOnOwnCollision(
            new TimeoutException("the owner never answered"), ReleasePath));
}

using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The severity of a FAILED delete, pinned in both directions.
///
/// <para>The chain in <c>MeshExtensions.DeleteNode</c>'s catch reports a torn subtree loudly and
/// everything else quietly, and every pair in it tests <c>partial.Count</c> to tell those apart —
/// unauthorized is Error only once nodes were removed (#1128), cancelled likewise (#2182). The
/// not-found branch never applied that test, so a delete whose target was already gone was reported
/// at the same severity as a half-deleted subtree. Through a shared fingerprint those lines kept
/// REOPENING <c>Systemorph/MeshWeaver#1422</c>, which had been closed on its own subject twice.</para>
///
/// <para>Both directions matter. Demoting too much is the worse failure: an <c>unexpected</c>
/// delete failure, or a not-found that already removed nodes, is exactly when loud is correct.</para>
/// </summary>
public class DeleteFailureSeverityTest
{
    [Fact]
    public void NotFound_HavingRemovedNothing_IsNotAnError()
        // The #1422 line verbatim: `[DeleteNode] not-found path=… partial-deleted=0`.
        => MeshExtensions.DeleteFailureLevel(isNotFound: true, partialCount: 0)
            .Should().Be(LogLevel.Warning);

    [Fact]
    public void NotFound_THAT_ALREADY_REMOVED_NODES_StaysAnError()
        // A torn subtree is the condition this chain exists to shout about, and a not-found is no
        // exception to it: those nodes are gone and the rest never will be.
        => MeshExtensions.DeleteFailureLevel(isNotFound: true, partialCount: 3)
            .Should().Be(LogLevel.Error);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void Unexpected_StaysAnError_WhateverWasRemoved(int partial)
        // `unexpected` is by definition a failure nobody classified. Quietening it on the same
        // partial-count test would hide the one case with no known shape at all.
        => MeshExtensions.DeleteFailureLevel(isNotFound: false, partialCount: partial)
            .Should().Be(LogLevel.Error);
}

using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="ApiTokenService.DeleteToken"/> reports WHAT IT REMOVED, not merely that it ran.
///
/// <para><b>The defect.</b> <c>DeleteToken</c> projected the delete with
/// <c>.Select(_ =&gt; true)</c> and recovered faults to <c>false</c>. That read correctly only while
/// deleting an absent path FAULTED: MeshWeaver#4668 made it a success emitting <c>false</c>, so the
/// projection began overwriting the one bit the new contract added and every absent path reported a
/// removal that never happened.</para>
///
/// <para><b>Why it is worth a test rather than a one-line tidy.</b> The damage is not the wrong
/// value, it is what the wrong value SILENCES. <c>true</c> from an unconditional projection is a
/// constant, so every assertion that <c>DeleteToken</c> returned <c>true</c> becomes unfailable —
/// including the stale-read regression test whose whole subject is <c>DeleteToken</c> resolving a
/// path the token is not at. A defect that turns another test vacuous outlives its own fix.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the projection comes back: <see cref="ASecondDeleteOfTheSamePath_RemovesNothing"/>
/// goes red on the second call, and it is the same path both times, so no path-shape or
/// permission story can explain the difference away. <see cref="TheFirstDeleteOfALiveToken_RemovesIt"/>
/// is the control in the other direction — it fails if the value is hardwired to <c>false</c>, so
/// the pair cannot both be satisfied by a constant.</para>
/// </summary>
public class DeleteTokenReportsWhatItRemovedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    [Fact]
    public async Task TheFirstDeleteOfALiveToken_RemovesIt()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "delete-reports-user", "Delete Reports", "delete-reports@example.com", "First").Should().Emit();

        var removed = await service.DeleteToken(created.Node.Path).Should().Emit();

        removed.Should().BeTrue(
            "the token node was there and this call is what took it away — a delete that removed "
            + "something must say so, or the value carries no information at all");
    }

    [Fact]
    public async Task ASecondDeleteOfTheSamePath_RemovesNothing()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "delete-reports-twice", "Delete Twice", "delete-twice@example.com", "Second").Should().Emit();
        var path = created.Node.Path;

        var first = await service.DeleteToken(path).Should().Emit();
        var second = await service.DeleteToken(path).Should().Emit();

        first.Should().BeTrue("the first call is the one that removed the token");
        second.Should().BeFalse(
            "the node was already gone, so this call removed nothing. It is not an error — the "
            + "postcondition already held — but reporting it as a removal is how an unconditional "
            + "`.Select(_ => true)` hides both a no-op and every path-resolution bug behind it");
    }

    [Fact]
    public async Task APathThatNeverHeldAToken_RemovesNothing()
    {
        var removed = await GetService()
            .DeleteToken("delete-reports-ghost/ApiToken/never-minted").Should().Emit();

        removed.Should().BeFalse(
            "nothing was ever stored there, so nothing was removed");
    }

    /// <summary>
    /// The value has to survive all the way to the screen, or the fix is invisible to the only
    /// person it is for. <c>ApiTokensSettingsTab</c> subscribed with <c>_ =&gt; "Deleted token"</c>
    /// and so announced a removal for a token that was already gone — the second admin on the same
    /// live list, or the row clicked twice. These drive the SAME factored composition the click
    /// handler subscribes to (as the revoke button's test already does), so a regression in the
    /// handler cannot pass them.
    /// </summary>
    [Fact]
    public async Task TheTabReportsARealRemovalAsRemoved()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "delete-tab-user", "Tab User", "tab@example.com", "Tab").Should().Emit();

        var outcome = await ApiTokensSettingsTab
            .Delete(service, created.Node.Path, "Tab").Should().Emit();

        outcome.Result.Should().Be(ApiTokensSettingsTab.TokenDeleteResult.Removed);
        outcome.Label.Should().Be("Tab");
    }

    [Fact]
    public async Task TheTabDoesNotAnnounceARemovalForATokenThatWasAlreadyGone()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "delete-tab-twice", "Tab Twice", "tab-twice@example.com", "Twice").Should().Emit();

        var first = await ApiTokensSettingsTab
            .Delete(service, created.Node.Path, "Twice").Should().Emit();
        var second = await ApiTokensSettingsTab
            .Delete(service, created.Node.Path, "Twice").Should().Emit();

        first.Result.Should().Be(ApiTokensSettingsTab.TokenDeleteResult.Removed);
        second.Result.Should().Be(ApiTokensSettingsTab.TokenDeleteResult.AlreadyGone,
            "nobody here took that token away, so the screen must not say it did — and "
            + "AlreadyGone is not Failed either: nothing went wrong, there was simply nothing "
            + "to do");
    }
}

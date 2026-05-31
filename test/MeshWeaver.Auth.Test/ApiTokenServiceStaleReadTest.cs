using System;
using System.Linq;
using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Stale-read coverage tests for <see cref="ApiTokenService.RevokeToken"/> and
/// <see cref="ApiTokenService.DeleteToken"/>.
///
/// The bug being guarded: the previous implementation looked up the token's
/// authoritative state via
/// <c>Observable.FromAsync(() => meshQuery.QueryAsync(path:X).FirstOrDefaultAsync())</c>.
/// <c>QueryAsync</c> goes through the lagged read-side index (eventually consistent),
/// so right after a write the lookup can return null and the operation no-ops. The
/// fix uses <c>hub.GetMeshNode(path)</c> which goes through the per-node hub's
/// <c>MeshNodeReference</c> reducer — authoritative, never stale.
///
/// The tests below issue a Create immediately followed by Revoke/Delete, with no
/// sleep, no flush, no intermediate query. Under stale-read the second call can't
/// find the just-created token; under the fix it always does.
/// </summary>
public class ApiTokenServiceStaleReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()
        );

    /// <summary>
    /// SHOULD-FAIL-IF: <c>RevokeToken</c> resolves the token via
    /// <c>QueryAsync(path:X).FirstOrDefaultAsync()</c> — that goes through the lagged
    /// read-side index and can return null right after a write. The fix uses
    /// <c>hub.GetMeshNode(path)</c> (authoritative reducer) so the just-created
    /// token is always visible.
    /// </summary>
    [Fact]
    public void RevokeToken_ImmediatelyAfterCreate_SeesTheNewToken()
    {
        var service = GetService();

        // Create the token via the reactive surface — emits once both the user-scoped
        // node and the index pointer commit.
        var creation = service.CreateToken(
                "user-stale", "Stale Reader", "stale@test.com", "Token A")
            .Should().Emit();

        // No flush, no sleep — straight to revoke. Under the old QueryAsync-based
        // path the read-side index can lag and RevokeToken returns false because it
        // can't find the node it just created.
        var ok = service.RevokeToken(creation.Node.Path).Should().Emit();

        ok.Should().BeTrue("RevokeToken must observe the just-created token (no stale lag)");

        // Confirm the revoke actually took effect — ValidateToken must reject the now-revoked token.
        // ApiToken paths don't have a per-node hub, so we can't subscribe to MeshNodeReference;
        // ValidateToken exercises the same authoritative read path used in production auth.
        // It reads via the live GetApiTokenByHash synced query, whose snapshot can lag the
        // just-applied revoke — re-issue on a 50 ms interval until the token reads as null.
        var validated = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(creation.RawToken).Take(1))
            .Should().Match(v => v is null, "revoked tokens must not validate");
        validated.Should().BeNull("revoked tokens must not validate");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: <c>DeleteToken</c> resolves the token via
    /// <c>QueryAsync(path:X).FirstOrDefaultAsync()</c> — the lagged index can return
    /// null right after a Create and DeleteToken silently succeeds without removing
    /// the index entry, leaving an orphan that future ValidateToken calls might hit.
    /// </summary>
    [Fact]
    public void DeleteToken_ImmediatelyAfterCreate_RemovesTheNewToken()
    {
        var service = GetService();

        var creation = service.CreateToken(
                "user-stale-del", "Stale Deleter", "del@test.com", "Token to Delete")
            .Should().Emit();

        // No sleep — straight to delete. Stale-read returns null → DeleteToken
        // resolves to false silently.
        var ok = service.DeleteToken(creation.Node.Path).Should().Emit();

        ok.Should().BeTrue("DeleteToken must observe the just-created token (no stale lag)");

        // The token must no longer validate — ValidateToken returns null when the index
        // pointer is gone (which DeleteToken removed alongside the user-namespace node).
        var validated = service.ValidateToken(creation.RawToken).Should().Emit();
        validated.Should().BeNull("DeleteToken should have removed the token's index pointer");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: rapid create-then-revoke loops surface stale reads when run
    /// back-to-back. Same bug class, different concurrency profile — this exercises
    /// the case where the read-side index has barely caught up between iterations.
    /// </summary>
    [Fact]
    public void RevokeToken_RepeatedCreateRevoke_AlwaysSeesEachToken()
    {
        var service = GetService();

        for (var i = 0; i < 5; i++)
        {
            var creation = service.CreateToken(
                    $"user-rapid-{i}", "Rapid Tester", $"rapid{i}@test.com", $"Token {i}")
                .Should().Emit();

            var ok = service.RevokeToken(creation.Node.Path).Should().Emit();

            ok.Should().BeTrue($"iteration {i}: RevokeToken must see the just-created token");
        }
    }

    /// <summary>
    /// SHOULD-FAIL-IF: <c>RevokeToken</c> doesn't propagate to a subsequent
    /// <c>ValidateToken</c> because the in-flight stale read of the token returned a
    /// snapshot that didn't have IsRevoked=true at the moment of the validation
    /// (or because the revoke itself never landed due to the stale-read bug).
    /// </summary>
    [Fact]
    public void RevokeToken_AfterImmediateValidate_BlocksFutureValidation()
    {
        var service = GetService();

        var creation = service.CreateToken(
                "user-validate", "Validator", "v@test.com", "Validate")
            .Should().Emit();

        // Confirm the token validates first (so the test failure mode below is
        // strictly "revoke didn't take effect" not "create never landed").
        // ValidateToken reads the live GetApiTokenByHash synced query, whose
        // first snapshot can be empty right after the create — re-issue on a
        // 50 ms interval until the token becomes visible.
        var beforeRevoke = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(creation.RawToken).Take(1))
            .Should().Match(v => v is not null);
        beforeRevoke.Should().NotBeNull();

        var ok = service.RevokeToken(creation.Node.Path).Should().Emit();
        ok.Should().BeTrue();

        // Re-issue until the revoke propagates into the synced view (validates as null).
        var afterRevoke = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(creation.RawToken).Take(1))
            .Should().Match(v => v is null, "revoked tokens must not validate");
        afterRevoke.Should().BeNull("revoked tokens must not validate");
    }
}

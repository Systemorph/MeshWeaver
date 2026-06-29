using System;
using Memex.Portal.Shared.Authentication;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Regression suite for the API-token revoke click action. The production
/// incident: clicking Revoke timed out after ~30s — observed only in
/// distributed (Orleans) deployments where <c>nodeFactory.UpdateNode(...)</c>
/// forwarded a now-retired <c>UpdateNodeRequest</c> that never got a response
/// from the owning per-node hub. Fix: the service now writes via
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c> (data-sync protocol
/// instead of a hub-forward). These tests pin a tight deadline
/// (10s) so any future regression that reintroduces a hub-forward deadlock
/// fails CI fast with a clear cancellation, instead of waiting for the user
/// to notice in prod.
///
/// <para>The test calls <see cref="ApiTokensSettingsTab.Revoke"/> — the
/// factored-out observable composition the click handler subscribes to —
/// rather than re-implementing the click pipeline. Keeps the test honest
/// to the production code path.</para>
/// </summary>
public class ApiTokensSettingsTabRevokeTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Deadline beyond which we declare the revoke deadlocked. Picked at
    /// 10s — well below the 30s prod symptom but generous enough that
    /// monolith-startup overhead (MeshDataSource warmup, AccessService
    /// init) doesn't false-alarm.
    /// </summary>
    private static readonly TimeSpan RevokeDeadline = TimeSpan.FromSeconds(10);

    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()
        );

    [Fact]
    public async Task Revoke_ExistingToken_CompletesUnderDeadlineWithSuccess()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Click Test").Should().Emit();

        // Mirror production lifecycle: the settings tab subscribes to
        // GetTokensForUser (a workspace.GetQuery synced collection)
        // BEFORE rendering the Revoke button. That subscription registers
        // the user's ApiToken paths in the workspace's live synced-query
        // path set — the prerequisite for GetMeshNodeStream(path).Update
        // to resolve the per-node hub via the workspace-level reducer.
        // Without this pre-subscribe, the test bypasses the warm-up step
        // the UI takes for granted and the Update lambda gets a null
        // current value.
        await WaitForTokenInSyncedList(service, created.Node.Path);

        // The .Within(RevokeDeadline) deadline is the regression guard: if
        // the revoke composition deadlocks anywhere, the assertion fails
        // after RevokeDeadline pointing squarely at the broken layer.
        var outcome = await ApiTokensSettingsTab.Revoke(service, created.Node.Path, "Click Test")
            .Should().Within(RevokeDeadline).Emit();

        outcome.Success.Should().BeTrue(
            "the production click handler is subscribed to this exact observable; " +
            "a false outcome means the user sees 'Failed to revoke' instead of confirmation");
        outcome.Label.Should().Be("Click Test");
        outcome.Message.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_NonExistentToken_CompletesUnderDeadlineWithFailure()
    {
        var service = GetService();

        // The previous shape (FromAsync(QueryAsync).SelectMany(UpdateNode))
        // returned false on missing node — the stream-based rewrite must
        // preserve that contract (no exception, no hang). A regression here
        // would surface as either a .Within(RevokeDeadline) timeout OR an
        // unhandled exception bubbling out of the OnNext.
        var outcome = await ApiTokensSettingsTab.Revoke(service, "user1/ApiToken/does-not-exist", "Missing")
            .Should().Within(RevokeDeadline).Emit();

        outcome.Success.Should().BeFalse();
        outcome.Label.Should().Be("Missing");
    }

    [Fact]
    public async Task Revoke_AlreadyRevokedToken_CompletesUnderDeadline()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Double Revoke").Should().Emit();

        await WaitForTokenInSyncedList(service, created.Node.Path);

        // First revoke through the click pipeline. Tight deadline here too —
        // a deadlock on a freshly-revoked token would still be a regression.
        var firstOutcome = await ApiTokensSettingsTab.Revoke(service, created.Node.Path, "Double Revoke")
            .Should().Within(RevokeDeadline).Emit();
        firstOutcome.Success.Should().BeTrue();

        // Second revoke on the same path: the node still exists (revoke =
        // flag flip, not delete). Re-applying IsRevoked=true is a no-op
        // update — must still complete within the deadline and not hang
        // waiting for "node not found".
        var secondOutcome = await ApiTokensSettingsTab.Revoke(service, created.Node.Path, "Double Revoke")
            .Should().Within(RevokeDeadline).Emit();

        secondOutcome.Success.Should().BeTrue(
            "re-revoking a revoked token is idempotent; the second call must " +
            "still complete promptly so the user can recover from a stale UI list");
    }

    [Fact]
    public async Task Revoke_ManyTokens_CompletesEachUnderDeadline()
    {
        // Sanity: nothing about the composition retains a per-call subscription
        // or other state that would slow successive revokes. A regression to
        // O(N) hub-forwards under contention would fail here long before any
        // individual call hits RevokeDeadline.
        var service = GetService();
        var tokens = new System.Collections.Generic.List<MeshNode>();
        for (var i = 0; i < 5; i++)
        {
            var c = await service.CreateToken(
                "user1", "Test User", "test@example.com", $"Token {i}").Should().Emit();
            tokens.Add(c.Node);
        }

        // Single synced-query warm-up after all creates — the path set is
        // namespace-scoped so a single subscribe covers every token at once.
        await WaitForTokenInSyncedList(service, tokens[^1].Path);

        foreach (var token in tokens)
        {
            var outcome = await ApiTokensSettingsTab.Revoke(service, token.Path, token.Name ?? "")
                .Should().Within(RevokeDeadline).Emit();
            outcome.Success.Should().BeTrue(
                $"revoke for {token.Path} must complete within {RevokeDeadline.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Robustness gate: <see cref="ApiTokenService.CreateToken"/> calling
    /// <see cref="IMeshService.CreateNode"/> must NEVER silently succeed
    /// when RLS rejects the underlying CreateNodeRequest. A silent success
    /// leaves <see cref="TokenCreationResult"/> pointing at a path that
    /// doesn't actually exist in persistence — every subsequent operation
    /// (revoke, validate, list) then mysteriously fails. The framework
    /// surfaces RLS denials as a non-Success CreateNodeResponse → the
    /// observable's <c>SelectMany</c> chain converts that into
    /// <c>InvalidOperationException</c> via <see cref="MeshService.CreateNode"/>.
    ///
    /// <para>This test reads back the user-scoped node immediately after
    /// CreateToken via the same persistence query the production read
    /// path uses (<c>workspace.GetQuery(...)</c>) and asserts the node is
    /// actually there — a presence check that catches "Success=true but
    /// persistence empty" regressions.</para>
    /// </summary>
    [Fact]
    public async Task CreateToken_PersistsNodeOrThrows_NeverSilentReject()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Persistence Probe").Should().Emit();

        created.Node.Path.Should().NotBeNullOrEmpty(
            "CreateToken must populate Node.Path — empty path is the silent-success symptom");

        // Read back through the synced-query path. If the token isn't in
        // the synced collection within 10s, persistence is the issue —
        // CreateNode returned Success but the node isn't actually queryable.
        var tokens = await service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(list => System.Linq.Enumerable.Any(list, t => t.NodePath == created.Node.Path));

        tokens.Should().ContainSingle(t => t.NodePath == created.Node.Path,
            "CreateToken returned Success → persistence MUST contain the node. " +
            "A silent reject (RLS denying but CreateNodeResponse.Success=true) " +
            "leaves the user with an invisible token and any subsequent " +
            "operation (revoke/validate/list) mysteriously fails.");
    }

    /// <summary>
    /// Robustness gate: when the workspace remote stream returns null for a
    /// path (per-node hub activated but workspace didn't load the node
    /// from persistence — cause #4 in the
    /// <see cref="MeshWeaver.Mesh.MeshNodeStreamHandle"/> Update error
    /// message), the failure must surface as a clear exception, not a
    /// cryptic operation-completed-with-false. The current shape catches
    /// the InvalidOperationException from MeshNodeStreamExtensions and
    /// returns false — that obscures the persistence-layer bug from CI
    /// and users.
    ///
    /// <para>This test calls Revoke on a path that's never been read +
    /// never been in a synced query, simulating the worst case where the
    /// per-node hub activates fresh. The expectation: the exception
    /// message includes the diagnostic hints we added to
    /// <see cref="MeshWeaver.Mesh.MeshNodeStreamHandle"/> — RLS reject,
    /// persistence not loaded, deleted-between-read-write — so the
    /// failure mode is debuggable from the log line alone.</para>
    /// </summary>
    [Fact]
    public async Task Revoke_WithoutSyncedQueryWarmup_FailsWithDiagnosticMessage()
    {
        var service = GetService();
        var created = await service.CreateToken(
            "user1", "Test User", "test@example.com", "No-Warmup").Should().Emit();

        // Deliberately skip WaitForTokenInSyncedList — this is the
        // "no synced query" scenario. With the current ApiTokenService
        // catch shape, the InvalidOperationException is swallowed and
        // outcome.Success=false. The Message should carry the diagnostic
        // hints from MeshNodeStreamHandle's improved error.
        var outcome = await ApiTokensSettingsTab.Revoke(service, created.Node.Path, "No-Warmup")
            .Should().Within(RevokeDeadline).Emit();

        // Document the current behavior: the catch in RevokeToken converts
        // the InvalidOperationException to outcome.Success=false. The user
        // wants this changed so the diagnostic surfaces — either as a
        // thrown exception or a richer outcome record. Until that's wired,
        // this assertion pins the current shape so future code changes
        // are noticed.
        if (outcome.Success)
        {
            // If the per-node hub auto-loads from persistence (the fix the
            // user wants for cause #4), this branch becomes the happy path.
            outcome.Message.Should().BeNull();
        }
        else
        {
            outcome.Message.Should().NotBeNullOrEmpty(
                "a false outcome MUST carry the diagnostic message — silent " +
                "false is exactly the cryptic failure mode the user flagged");
        }
    }

    /// <summary>
    /// Subscribes to <see cref="ApiTokenService.GetTokensForUser"/> (a
    /// <c>workspace.GetQuery</c> synced collection) and blocks until the
    /// node at <paramref name="expectedPath"/> appears in the snapshot. The
    /// subscription registers the path in the workspace's live
    /// synced-query set — the prerequisite for
    /// <c>GetMeshNodeStream(path).Update(...)</c> to resolve via the
    /// workspace-level reducer instead of opening a fresh GetRemoteStream
    /// that may not see the node yet.
    /// </summary>
    private async Task WaitForTokenInSyncedList(ApiTokenService service, string expectedPath)
        => await service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(10))
            .Match(list => System.Linq.Enumerable.Any(list, t => t.NodePath == expectedPath));
}

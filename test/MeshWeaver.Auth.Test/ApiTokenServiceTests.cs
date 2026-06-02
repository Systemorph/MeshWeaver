using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

public class ApiTokenServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()
        );

    [Fact]
    public void CreateToken_ReturnsTokenWithCorrectPrefix()
    {
        var result = GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Test Label").Should().Emit();

        result.RawToken.Should().StartWith("mw_");
        result.RawToken.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public void CreateToken_StoresNodeWithHashedContent()
    {
        var result = GetService().CreateToken(
            "user1", "Test User", "test@example.com", "My Token").Should().Emit();

        result.Node.Should().NotBeNull();
        result.Node.NodeType.Should().Be("ApiToken");
        result.Node.Namespace.Should().Be("user1/ApiToken");

        var apiToken = result.Node.Content as ApiToken;
        apiToken.Should().NotBeNull();
        apiToken!.TokenHash.Should().NotBeNullOrEmpty();
        apiToken.TokenHash.Should().NotContain("mw_");
        apiToken.UserId.Should().Be("user1");
        apiToken.UserName.Should().Be("Test User");
        apiToken.UserEmail.Should().Be("test@example.com");
        apiToken.Label.Should().Be("My Token");
        apiToken.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public void CreateToken_PersistsNodeToStorage()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").Should().Emit();

        // ApiToken nodes don't activate per-node hubs (IsSatelliteType), so
        // ReadNodeAsync would hang waiting for a route — read via the same
        // mesh-level index ApiTokenService uses internally. Wait on the live
        // query stream until the create lands (absorbs read-side index lag).
        var stored = ObserveNode(result.Node.Path!).Should().Match(n => n is not null);
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public void ValidateToken_ValidToken_ReturnsApiToken()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Valid Token").Should().Emit();

        // ValidateToken reads via the live GetApiTokenByHash synced query,
        // whose first snapshot can be empty right after the create (read-side
        // index lag). Re-issue on a 50 ms interval until the token becomes
        // visible — same primitive as ValidateToken_RevokedToken_ReturnsNull.
        var validated = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
        validated.UserName.Should().Be("Test User");
        validated.Label.Should().Be("Valid Token");
    }

    [Fact]
    public void ValidateToken_InvalidToken_ReturnsNull()
    {
        var result = GetService().ValidateToken("mw_invalidtokenvalue123").Should().Emit();

        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_EmptyToken_ReturnsNull()
    {
        var result = GetService().ValidateToken("").Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_NullToken_ReturnsNull()
    {
        var result = GetService().ValidateToken(null!).Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_TokenWithoutPrefix_ReturnsNull()
    {
        var result = GetService().ValidateToken("notaprefixedtoken").Should().Emit();
        result.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsNull()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Expired",
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1)).Should().Emit();

        var validated = service.ValidateToken(result.RawToken).Should().Emit();

        validated.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_RevokedToken_ReturnsNull()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").Should().Emit();

        service.RevokeToken(result.Node.Path).Should().Emit();

        // ApiToken nodes don't activate per-node hubs (see 1e22b3cc3), so
        // ReadNode(path).MeshNodeReference would hang. Verify revoke through
        // the production read path — ValidateToken reads via the
        // mesh-level index that QueryAsync(path:X) uses. Stream-poll the
        // request/response on a 50 ms interval until the token reads as null,
        // absorbing read-side index lag — replaces a hand-rolled do/while +
        // Task.Delay(50).
        var validated = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is null);

        validated.Should().BeNull();
    }

    [Fact]
    public void RevokeToken_ExistingToken_ReturnsTrue()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").Should().Emit();

        var revoked = service.RevokeToken(result.Node.Path).Should().Emit();

        revoked.Should().BeTrue();
    }

    [Fact]
    public void RevokeToken_NonexistentPath_ReturnsFalse()
    {
        var revoked = GetService().RevokeToken("ApiToken/nonexistent").Should().Emit();

        revoked.Should().BeFalse();
    }

    [Fact]
    public void GetTokensForUser_ReturnsOnlyUserTokens()
    {
        var service = GetService();
        service.CreateToken("user1", "User One", "u1@test.com", "Token A").Should().Emit();
        service.CreateToken("user1", "User One", "u1@test.com", "Token B").Should().Emit();
        service.CreateToken("user2", "User Two", "u2@test.com", "Token C").Should().Emit();

        var user1Tokens = service.GetTokensForUser("user1").Should().Match(t => t.Count == 2);
        var user2Tokens = service.GetTokensForUser("user2").Should().Match(t => t.Count == 1);

        user1Tokens.Should().HaveCount(2);
        user1Tokens.Select(t => t.Label).Should().Contain("Token A").And.Contain("Token B");

        user2Tokens.Should().HaveCount(1);
        user2Tokens[0].Label.Should().Be("Token C");
    }

    [Fact]
    public void GetTokensForUser_NeverExposesFullHash()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").Should().Emit();

        var tokens = service.GetTokensForUser("user1").Should().Match(t => t.Count == 1);

        tokens.Should().HaveCount(1);
        var tokenInfo = tokens[0];
        tokenInfo.HashPrefix.Length.Should().BeLessThanOrEqualTo(8);
        var fullHash = ApiTokenService.HashToken(result.RawToken);
        fullHash.Length.Should().Be(64);
        tokenInfo.HashPrefix.Should().Be(fullHash[..8]);
    }

    // ── DeleteToken (previously zero coverage; the old hub.Post pattern would deadlock) ──

    [Fact]
    public void DeleteToken_RemovesNodeFromStorage()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").Should().Emit();

        service.DeleteToken(result.Node.Path).Should().Emit();

        // ApiToken nodes don't activate per-node hubs — read via the same
        // mesh-level path:X query the production service uses, waiting on the
        // live stream to absorb read-side index lag.
        var stored = ObserveNode(result.Node.Path).Should().Match(n => n is null);
        stored.Should().BeNull();
    }

    [Fact]
    public void DeleteToken_AfterDelete_ValidateReturnsNull()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").Should().Emit();

        service.DeleteToken(result.Node.Path).Should().Emit();

        // Stream-poll the request/response until null — replaces a hand-rolled
        // do/while + Task.Delay(50) loop.
        var validated = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is null);

        validated.Should().BeNull();
    }

    [Fact]
    public void DeleteToken_AlsoRemovesIndexEntry()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Index Delete").Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        var hashPrefix = apiToken!.TokenHash[..12];
        var indexPath = $"ApiToken/{hashPrefix}";

        service.DeleteToken(result.Node.Path).Should().Emit();

        // Same primitive as DeleteToken_RemovesNodeFromStorage — query, not
        // ReadNodeAsync (no per-node hub at ApiToken/{hash} either).
        var index = ObserveNode(indexPath).Should().Match(n => n is null);
        index.Should().BeNull();
    }

    [Fact]
    public void DeleteToken_NonexistentPath_Completes()
    {
        var result = GetService().DeleteToken("user1/ApiToken/nonexistent").Should().Emit();
        result.Should().BeFalse();
    }

    // ── GetTokensForUser edge cases ──

    [Fact]
    public void GetTokensForUser_EmptyUser_ReturnsEmpty()
    {
        var tokens = GetService().GetTokensForUser("nobody").Should().Emit();

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void GetTokensForUser_RevokedToken_StillAppearsAsRevoked()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Revoke Me").Should().Emit();
        service.RevokeToken(result.Node.Path).Should().Emit();

        // 🚨 Subscribe ONCE to the live synced-query stream and wait for the
        // condition via Should().Match(...). Replaces the prior
        // Observable.Interval(50ms) polling — re-subscribing to GetTokensForUser
        // every tick raced the synced-query Replay(1) cache (project_synced_query_race.md):
        // each poll hit the cache's buffered Initial snapshot rather than waiting
        // for the live Updated emission from the revoke's NotifyChange.
        var tokens = service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(t => t.Any(x => x.Label == "Revoke Me" && x.IsRevoked));

        tokens.Should().ContainSingle(t => t.Label == "Revoke Me" && t.IsRevoked);
    }

    [Fact]
    public void GetTokensForUser_DeletedToken_DoesNotAppear()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Delete Me").Should().Emit();
        service.DeleteToken(result.Node.Path).Should().Emit();

        // Single live subscription to the synced-query stream — see comment on
        // GetTokensForUser_RevokedToken_StillAppearsAsRevoked for why polling
        // re-subscriptions race the Replay(1) cache.
        var tokens = service.GetTokensForUser("user1")
            .Should().Within(TimeSpan.FromSeconds(15))
            .Match(t => !t.Any(x => x.Label == "Delete Me"));

        tokens.Should().NotContain(t => t.Label == "Delete Me");
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        var hash1 = ApiTokenService.HashToken("mw_testtoken123");
        var hash2 = ApiTokenService.HashToken("mw_testtoken123");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashToken_ProducesSha256HexString()
    {
        var hash = ApiTokenService.HashToken("mw_testtoken123");

        hash.Length.Should().Be(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashToken_DifferentTokens_ProduceDifferentHashes()
    {
        var hash1 = ApiTokenService.HashToken("mw_token1");
        var hash2 = ApiTokenService.HashToken("mw_token2");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void CreateToken_WithExpiry_SetsExpiresAt()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var result = GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Expiring",
            expiresAt: expiry).Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt!.Value.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void CreateToken_WithoutExpiry_ExpiresAtIsNull()
    {
        var result = GetService().CreateToken(
            "user1", "Test User", "test@example.com", "No Expiry").Should().Emit();

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void CreateToken_EachTokenIsUnique()
    {
        var service = GetService();
        var result1 = service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 1").Should().Emit();
        var result2 = service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 2").Should().Emit();

        result1.RawToken.Should().NotBe(result2.RawToken);
    }

    [Fact]
    public void ValidateToken_FutureExpiry_ReturnsApiToken()
    {
        var service = GetService();
        var result = service.CreateToken(
            "user1", "Test User", "test@example.com", "Future",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30)).Should().Emit();

        // Live synced-query read — re-issue until the just-created token
        // lands (absorbs read-side index lag).
        var validated = Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).Take(1))
            .Should().Match(v => v is not null);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
    }

    /// <summary>
    /// Folds the live <see cref="IMeshService.Query"/> stream for the MeshNode at
    /// <paramref name="path"/> into a single <c>MeshNode?</c> — Initial / Added / Updated /
    /// Removed events collapse via <c>.Scan</c>. Callers assert on the result with
    /// <c>.Should().Match(...)</c>, which blocks (≤ the assertion timeout) for the first
    /// snapshot satisfying the predicate. No polling, no <c>Task.Delay</c>.
    /// <para>
    /// Required for ApiToken paths because those nodes are <c>IsSatelliteType</c> and have no
    /// per-node hub — the test base's <c>ReadNodeAsync</c> (and <c>GetMeshNodeStream(path)</c>)
    /// would hang for 30s waiting for a route. The mesh-level <c>Query</c> reads the
    /// authoritative persistence state through the same pipeline production <c>ApiTokenService</c>
    /// uses for its own reads, with live change-notifier deltas instead of a polling loop.
    /// </para>
    /// </summary>
    private IObservable<MeshNode?> ObserveNode(string path)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}", WellKnownUsers.System))
            .Scan((MeshNode?)null, (current, change) => change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset =>
                    change.Items.FirstOrDefault(),
                QueryChangeType.Added or QueryChangeType.Updated =>
                    change.Items.FirstOrDefault() ?? current,
                QueryChangeType.Removed => null,
                _ => current,
            });
    }
}

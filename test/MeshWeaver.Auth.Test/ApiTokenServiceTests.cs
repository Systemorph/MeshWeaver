using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

    private CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateToken_ReturnsTokenWithCorrectPrefix()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Test Label").FirstAsync().ToTask(CT);

        result.RawToken.Should().StartWith("mw_");
        result.RawToken.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task CreateToken_StoresNodeWithHashedContent()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "My Token").FirstAsync().ToTask(CT);

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
    public async Task CreateToken_PersistsNodeToStorage()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").FirstAsync().ToTask(CT);

        // ApiToken nodes don't activate per-node hubs (IsSatelliteType), so
        // ReadNodeAsync would hang waiting for a route — read via the same
        // mesh-level index ApiTokenService uses internally. Poll briefly to
        // absorb read-side index lag (the create just landed).
        var stored = await PollQueryAsync(result.Node.Path!, n => n is not null, CT);
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public async Task ValidateToken_ValidToken_ReturnsApiToken()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Valid Token").FirstAsync().ToTask(CT);

        var validated = await service.ValidateToken(result.RawToken).FirstAsync().ToTask(CT);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
        validated.UserName.Should().Be("Test User");
        validated.Label.Should().Be("Valid Token");
    }

    [Fact]
    public async Task ValidateToken_InvalidToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken("mw_invalidtokenvalue123").FirstAsync().ToTask(CT);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_EmptyToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken("").FirstAsync().ToTask(CT);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_NullToken_ReturnsNull()
    {
        var result = await GetService().ValidateToken(null!).FirstAsync().ToTask(CT);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_TokenWithoutPrefix_ReturnsNull()
    {
        var result = await GetService().ValidateToken("notaprefixedtoken").FirstAsync().ToTask(CT);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_ExpiredToken_ReturnsNull()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Expired",
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1)).FirstAsync().ToTask(CT);

        var validated = await service.ValidateToken(result.RawToken).FirstAsync().ToTask(CT);

        validated.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_RevokedToken_ReturnsNull()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").FirstAsync().ToTask(CT);

        await service.RevokeToken(result.Node.Path).FirstAsync().ToTask(CT);

        // ApiToken nodes don't activate per-node hubs (see 1e22b3cc3), so
        // ReadNode(path).MeshNodeReference would hang. Verify revoke through
        // the production read path — ValidateToken reads via the
        // mesh-level index that QueryAsync(path:X) uses. Stream-poll the
        // request/response on a 50 ms interval until the token reads as null,
        // absorbing read-side index lag — replaces a hand-rolled do/while +
        // Task.Delay(50).
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).FirstAsync())
            .Where(v => v is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(CT);

        validated.Should().BeNull();
    }

    [Fact]
    public async Task RevokeToken_ExistingToken_ReturnsTrue()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Revoke").FirstAsync().ToTask(CT);

        var revoked = await service.RevokeToken(result.Node.Path).FirstAsync().ToTask(CT);

        revoked.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeToken_NonexistentPath_ReturnsFalse()
    {
        var revoked = await GetService().RevokeToken("ApiToken/nonexistent").FirstAsync().ToTask(CT);

        revoked.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokensForUser_ReturnsOnlyUserTokens()
    {
        var service = GetService();
        await service.CreateToken("user1", "User One", "u1@test.com", "Token A").FirstAsync().ToTask(CT);
        await service.CreateToken("user1", "User One", "u1@test.com", "Token B").FirstAsync().ToTask(CT);
        await service.CreateToken("user2", "User Two", "u2@test.com", "Token C").FirstAsync().ToTask(CT);

        var user1Tokens = await service.GetTokensForUser("user1").FirstAsync().ToTask(CT);
        var user2Tokens = await service.GetTokensForUser("user2").FirstAsync().ToTask(CT);

        user1Tokens.Should().HaveCount(2);
        user1Tokens.Select(t => t.Label).Should().Contain("Token A").And.Contain("Token B");

        user2Tokens.Should().HaveCount(1);
        user2Tokens[0].Label.Should().Be("Token C");
    }

    [Fact]
    public async Task GetTokensForUser_NeverExposesFullHash()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Test").FirstAsync().ToTask(CT);

        var tokens = await service.GetTokensForUser("user1").FirstAsync().ToTask(CT);

        tokens.Should().HaveCount(1);
        var tokenInfo = tokens[0];
        tokenInfo.HashPrefix.Length.Should().BeLessThanOrEqualTo(8);
        var fullHash = ApiTokenService.HashToken(result.RawToken);
        fullHash.Length.Should().Be(64);
        tokenInfo.HashPrefix.Should().Be(fullHash[..8]);
    }

    // ── DeleteToken (previously zero coverage; the old hub.Post pattern would deadlock) ──

    [Fact]
    public async Task DeleteToken_RemovesNodeFromStorage()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").FirstAsync().ToTask(CT);

        await service.DeleteToken(result.Node.Path).FirstAsync().ToTask(CT);

        // ApiToken nodes don't activate per-node hubs — read via the same
        // mesh-level path:X query the production service uses, polling to
        // absorb read-side index lag.
        var stored = await PollQueryAsync(result.Node.Path, n => n is null, CT);
        stored.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_AfterDelete_ValidateReturnsNull()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "To Delete").FirstAsync().ToTask(CT);

        await service.DeleteToken(result.Node.Path).FirstAsync().ToTask(CT);

        // Stream-poll the request/response until null — replaces a hand-rolled
        // do/while + Task.Delay(50) loop.
        var validated = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).FirstAsync())
            .Where(v => v is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(CT);

        validated.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_AlsoRemovesIndexEntry()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Index Delete").FirstAsync().ToTask(CT);

        var apiToken = result.Node.Content as ApiToken;
        var hashPrefix = apiToken!.TokenHash[..12];
        var indexPath = $"ApiToken/{hashPrefix}";

        await service.DeleteToken(result.Node.Path).FirstAsync().ToTask(CT);

        // Same primitive as DeleteToken_RemovesNodeFromStorage — query, not
        // ReadNodeAsync (no per-node hub at ApiToken/{hash} either).
        var index = await PollQueryAsync(indexPath, n => n is null, CT);
        index.Should().BeNull();
    }

    [Fact]
    public async Task DeleteToken_NonexistentPath_Completes()
    {
        var result = await GetService().DeleteToken("user1/ApiToken/nonexistent").FirstAsync().ToTask(CT);
        result.Should().BeFalse();
    }

    // ── GetTokensForUser edge cases ──

    [Fact]
    public async Task GetTokensForUser_EmptyUser_ReturnsEmpty()
    {
        var tokens = await GetService().GetTokensForUser("nobody").FirstAsync().ToTask(CT);

        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTokensForUser_RevokedToken_StillAppearsAsRevoked()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Revoke Me").FirstAsync().ToTask(CT);
        await service.RevokeToken(result.Node.Path).FirstAsync().ToTask(CT);

        // First wait for the revoke to land in the read-side index (the
        // authoritative path ValidateToken takes) — once null returns, the
        // persistence layer has the new state. Then read tokens via the
        // synced query. Without this two-step gate, the synced-query cache
        // was racing with the revoke commit (see
        // project_synced_query_race.md) — under CI load the first emission
        // of GetTokensForUser was the pre-revoke snapshot, and the
        // .Where(IsRevoked) filter waited forever for a re-emit that never
        // came.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).FirstAsync())
            .Where(v => v is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(CT);

        var tokens = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.GetTokensForUser("user1").FirstAsync())
            .Where(t => t.Any(x => x.IsRevoked))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(CT);

        tokens.Should().ContainSingle(t => t.Label == "Revoke Me" && t.IsRevoked);
    }

    [Fact]
    public async Task GetTokensForUser_DeletedToken_DoesNotAppear()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Delete Me").FirstAsync().ToTask(CT);
        await service.DeleteToken(result.Node.Path).FirstAsync().ToTask(CT);

        // First wait for the delete to land in the read-side index (the
        // authoritative path ValidateToken takes), THEN read tokens via
        // synced query. Two-step gate prevents the synced-query cache from
        // returning stale pre-delete snapshots — see comment on
        // GetTokensForUser_RevokedToken_StillAppearsAsRevoked for the
        // synced-query race rationale.
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(result.RawToken).FirstAsync())
            .Where(v => v is null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .ToTask(CT);

        var tokens = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.GetTokensForUser("user1").FirstAsync())
            .Where(t => !t.Any(x => x.Label == "Delete Me"))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(CT);

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
    public async Task CreateToken_WithExpiry_SetsExpiresAt()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "Expiring",
            expiresAt: expiry).FirstAsync().ToTask(CT);

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateToken_WithoutExpiry_ExpiresAtIsNull()
    {
        var result = await GetService().CreateToken(
            "user1", "Test User", "test@example.com", "No Expiry").FirstAsync().ToTask(CT);

        var apiToken = result.Node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateToken_EachTokenIsUnique()
    {
        var service = GetService();
        var result1 = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 1").FirstAsync().ToTask(CT);
        var result2 = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Token 2").FirstAsync().ToTask(CT);

        result1.RawToken.Should().NotBe(result2.RawToken);
    }

    [Fact]
    public async Task ValidateToken_FutureExpiry_ReturnsApiToken()
    {
        var service = GetService();
        var result = await service.CreateToken(
            "user1", "Test User", "test@example.com", "Future",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30)).FirstAsync().ToTask(CT);

        var validated = await service.ValidateToken(result.RawToken).FirstAsync().ToTask(CT);

        validated.Should().NotBeNull();
        validated!.UserId.Should().Be("user1");
    }

    /// <summary>
    /// Waits for the MeshNode at <paramref name="path"/> to satisfy <paramref name="condition"/>
    /// using the live <see cref="IMeshService.ObserveQuery"/> stream — Initial / Added / Updated /
    /// Removed events fold into a single <c>MeshNode?</c> that we filter with <c>.Where</c>. No
    /// polling, no <c>Task.Delay</c>: the timeout fires only if the condition genuinely never
    /// becomes true.
    /// <para>
    /// Required for ApiToken paths because those nodes are <c>IsSatelliteType</c> and have no
    /// per-node hub — the test base's <c>ReadNodeAsync</c> (and <c>GetMeshNodeStream(path)</c>)
    /// would hang for 30s waiting for a route. The mesh-level <c>ObserveQuery</c> reads the
    /// authoritative persistence state through the same pipeline production <c>ApiTokenService</c>
    /// uses for its own reads, with live change-notifier deltas instead of a polling loop.
    /// </para>
    /// </summary>
    private Task<MeshNode?> PollQueryAsync(string path, Func<MeshNode?, bool> condition, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return meshService
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}", WellKnownUsers.System))
            .Scan((MeshNode?)null, (current, change) => change.ChangeType switch
            {
                QueryChangeType.Initial or QueryChangeType.Reset =>
                    change.Items.FirstOrDefault(),
                QueryChangeType.Added or QueryChangeType.Updated =>
                    change.Items.FirstOrDefault() ?? current,
                QueryChangeType.Removed => null,
                _ => current,
            })
            .Where(node => condition(node))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync()
            .ToTask(ct);
    }
}

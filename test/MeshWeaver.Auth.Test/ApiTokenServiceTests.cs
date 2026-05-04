using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

        var stored = await ReadNodeAsync(result.Node.Path!, CT);
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
        // mesh-level index that QueryAsync(path:X) uses. Poll briefly to
        // absorb read-side index lag.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        ApiToken? validated;
        do
        {
            validated = await service.ValidateToken(result.RawToken).FirstAsync().ToTask(CT);
            if (validated is null) break;
            await Task.Delay(50, CT);
        } while (DateTimeOffset.UtcNow < deadline);

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
}

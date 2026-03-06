using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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

public class ApiTokenServiceTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshNodeFactory>(),
            Mesh.ServiceProvider.GetRequiredService<IMeshQuery>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>()
        );

    [Fact]
    public async Task CreateToken_ReturnsTokenWithCorrectPrefix()
    {
        var (rawToken, node) = await GetService().CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test Label");

        rawToken.Should().StartWith("mw_");
        rawToken.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task CreateToken_StoresNodeWithHashedContent()
    {
        var (rawToken, node) = await GetService().CreateTokenAsync(
            "user1", "Test User", "test@example.com", "My Token");

        node.Should().NotBeNull();
        node.NodeType.Should().Be("ApiToken");
        node.Namespace.Should().Be("ApiToken");

        var apiToken = node.Content as ApiToken;
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
        var (rawToken, node) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test");

        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var stored = await meshQuery.QueryAsync<MeshNode>($"path:{node.Path} scope:exact").FirstOrDefaultAsync();
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public async Task ValidateToken_ValidToken_ReturnsApiToken()
    {
        var service = GetService();
        var (rawToken, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Valid Token");

        var result = await service.ValidateTokenAsync(rawToken);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("user1");
        result.UserName.Should().Be("Test User");
        result.Label.Should().Be("Valid Token");
    }

    [Fact]
    public async Task ValidateToken_InvalidToken_ReturnsNull()
    {
        var result = await GetService().ValidateTokenAsync("mw_invalidtokenvalue123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_EmptyToken_ReturnsNull()
    {
        var result = await GetService().ValidateTokenAsync("");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_NullToken_ReturnsNull()
    {
        var result = await GetService().ValidateTokenAsync(null!);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_TokenWithoutPrefix_ReturnsNull()
    {
        var result = await GetService().ValidateTokenAsync("notaprefixedtoken");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_ExpiredToken_ReturnsNull()
    {
        var service = GetService();
        var (rawToken, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Expired",
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var result = await service.ValidateTokenAsync(rawToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_RevokedToken_ReturnsNull()
    {
        var service = GetService();
        var (rawToken, node) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "To Revoke");

        await service.RevokeTokenAsync(node.Path);

        // Give the hub a moment to process the UpdateNodeRequest
        await Task.Delay(100);

        var result = await service.ValidateTokenAsync(rawToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RevokeToken_ExistingToken_ReturnsTrue()
    {
        var service = GetService();
        var (_, node) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "To Revoke");

        var result = await service.RevokeTokenAsync(node.Path);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeToken_NonexistentPath_ReturnsFalse()
    {
        var result = await GetService().RevokeTokenAsync("ApiToken/nonexistent");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokensForUser_ReturnsOnlyUserTokens()
    {
        var service = GetService();
        await service.CreateTokenAsync("user1", "User One", "u1@test.com", "Token A");
        await service.CreateTokenAsync("user1", "User One", "u1@test.com", "Token B");
        await service.CreateTokenAsync("user2", "User Two", "u2@test.com", "Token C");

        var user1Tokens = await service.GetTokensForUserAsync("user1");
        var user2Tokens = await service.GetTokensForUserAsync("user2");

        user1Tokens.Should().HaveCount(2);
        user1Tokens.Select(t => t.Label).Should().Contain("Token A").And.Contain("Token B");

        user2Tokens.Should().HaveCount(1);
        user2Tokens[0].Label.Should().Be("Token C");
    }

    [Fact]
    public async Task GetTokensForUser_NeverExposesFullHash()
    {
        var service = GetService();
        var (rawToken, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test");

        var tokens = await service.GetTokensForUserAsync("user1");

        tokens.Should().HaveCount(1);
        var tokenInfo = tokens[0];
        tokenInfo.HashPrefix.Length.Should().BeLessThanOrEqualTo(8);
        var fullHash = ApiTokenService.HashToken(rawToken);
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
        var (_, node) = await GetService().CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Expiring",
            expiresAt: expiry);

        var apiToken = node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateToken_WithoutExpiry_ExpiresAtIsNull()
    {
        var (_, node) = await GetService().CreateTokenAsync(
            "user1", "Test User", "test@example.com", "No Expiry");

        var apiToken = node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateToken_EachTokenIsUnique()
    {
        var service = GetService();
        var (token1, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Token 1");
        var (token2, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Token 2");

        token1.Should().NotBe(token2);
    }

    [Fact]
    public async Task ValidateToken_FutureExpiry_ReturnsApiToken()
    {
        var service = GetService();
        var (rawToken, _) = await service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Future",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var result = await service.ValidateTokenAsync(rawToken);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("user1");
    }
}

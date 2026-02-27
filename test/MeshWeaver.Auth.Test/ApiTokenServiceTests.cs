using FluentAssertions;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Auth.Test;

public class ApiTokenServiceTests : IDisposable
{
    private readonly InMemoryPersistenceService _persistence;
    private readonly ApiTokenService _service;

    public ApiTokenServiceTests()
    {
        _persistence = new InMemoryPersistenceService();
        var logger = NullLogger<ApiTokenService>.Instance;
        _service = new ApiTokenService(_persistence, logger);
    }

    public void Dispose()
    {
        (_persistence as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CreateToken_ReturnsTokenWithCorrectPrefix()
    {
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test Label");

        rawToken.Should().StartWith("mw_");
        rawToken.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task CreateToken_StoresNodeWithHashedContent()
    {
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "My Token");

        node.Should().NotBeNull();
        node.NodeType.Should().Be("ApiToken");
        node.Namespace.Should().Be("ApiToken");

        // Verify the content is an ApiToken with hash (not raw token)
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
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test");

        var stored = await _persistence.GetNodeAsync(node.Path);
        stored.Should().NotBeNull();
        stored!.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public async Task ValidateToken_ValidToken_ReturnsApiToken()
    {
        var (rawToken, _) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Valid Token");

        var result = await _service.ValidateTokenAsync(rawToken);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("user1");
        result.UserName.Should().Be("Test User");
        result.Label.Should().Be("Valid Token");
    }

    [Fact]
    public async Task ValidateToken_InvalidToken_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync("mw_invalidtokenvalue123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_EmptyToken_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync("");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_NullToken_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync(null!);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_TokenWithoutPrefix_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync("notaprefixedtoken");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_ExpiredToken_ReturnsNull()
    {
        // Create a token that expired in the past
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Expired",
            expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var result = await _service.ValidateTokenAsync(rawToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_RevokedToken_ReturnsNull()
    {
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "To Revoke");

        // Revoke the token
        await _service.RevokeTokenAsync(node.Path);

        var result = await _service.ValidateTokenAsync(rawToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RevokeToken_ExistingToken_ReturnsTrue()
    {
        var (_, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "To Revoke");

        var result = await _service.RevokeTokenAsync(node.Path);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeToken_NonexistentPath_ReturnsFalse()
    {
        var result = await _service.RevokeTokenAsync("ApiToken/nonexistent");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetTokensForUser_ReturnsOnlyUserTokens()
    {
        await _service.CreateTokenAsync("user1", "User One", "u1@test.com", "Token A");
        await _service.CreateTokenAsync("user1", "User One", "u1@test.com", "Token B");
        await _service.CreateTokenAsync("user2", "User Two", "u2@test.com", "Token C");

        var user1Tokens = await _service.GetTokensForUserAsync("user1");
        var user2Tokens = await _service.GetTokensForUserAsync("user2");

        user1Tokens.Should().HaveCount(2);
        user1Tokens.Select(t => t.Label).Should().Contain("Token A").And.Contain("Token B");

        user2Tokens.Should().HaveCount(1);
        user2Tokens[0].Label.Should().Be("Token C");
    }

    [Fact]
    public async Task GetTokensForUser_NeverExposesFullHash()
    {
        var (rawToken, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Test");

        var tokens = await _service.GetTokensForUserAsync("user1");

        tokens.Should().HaveCount(1);
        var tokenInfo = tokens[0];
        tokenInfo.HashPrefix.Length.Should().BeLessOrEqualTo(8);
        // The full hash is at least 64 chars (SHA-256 hex)
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

        // SHA-256 produces 32 bytes = 64 hex chars
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
        var (_, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Expiring",
            expiresAt: expiry);

        var apiToken = node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateToken_WithoutExpiry_ExpiresAtIsNull()
    {
        var (_, node) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "No Expiry");

        var apiToken = node.Content as ApiToken;
        apiToken!.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateToken_EachTokenIsUnique()
    {
        var (token1, _) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Token 1");
        var (token2, _) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Token 2");

        token1.Should().NotBe(token2);
    }

    [Fact]
    public async Task ValidateToken_FutureExpiry_ReturnsApiToken()
    {
        var (rawToken, _) = await _service.CreateTokenAsync(
            "user1", "Test User", "test@example.com", "Future",
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        var result = await _service.ValidateTokenAsync(rawToken);

        result.Should().NotBeNull();
        result!.UserId.Should().Be("user1");
    }

    /// <summary>
    /// Minimal in-memory persistence for tests — only implements what ApiTokenService uses.
    /// </summary>
    private class InMemoryPersistenceService : IPersistenceService, IDisposable
    {
        private readonly Dictionary<string, MeshNode> _nodes = new();

        public Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
        {
            _nodes.TryGetValue(path, out var node);
            return Task.FromResult(node);
        }

        public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
        {
            var prefix = string.IsNullOrEmpty(parentPath) ? "" : parentPath + "/";
            foreach (var kv in _nodes)
            {
                if (string.IsNullOrEmpty(parentPath) || kv.Key.StartsWith(prefix))
                {
                    // Only direct children (no further slashes after prefix)
                    var rest = kv.Key[prefix.Length..];
                    if (!rest.Contains('/'))
                        yield return kv.Value;
                }
            }
        }

        public Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
        {
            _nodes[node.Path] = node;
            return Task.FromResult(node);
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            => Task.FromResult(_nodes.ContainsKey(path));

        public Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
        {
            if (recursive)
            {
                var toRemove = _nodes.Keys.Where(k => k == path || k.StartsWith(path + "/")).ToList();
                foreach (var key in toRemove) _nodes.Remove(key);
            }
            else
            {
                _nodes.Remove(path);
            }
            return Task.CompletedTask;
        }

        // Not used by ApiTokenService — stub implementations
        public IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath) => GetChildrenAsync(parentPath);
        public Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query) => throw new NotImplementedException();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath) => throw new NotImplementedException();
        public Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteCommentAsync(string commentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath) => throw new NotImplementedException();
        public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default) => throw new NotImplementedException();

        public void Dispose() { }
    }
}

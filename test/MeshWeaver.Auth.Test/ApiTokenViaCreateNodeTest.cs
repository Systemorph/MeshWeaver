using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

/// <summary>
/// Tests creating ApiToken nodes directly through <see cref="IMeshService.CreateNode"/>
/// (the public mesh write surface) — not via <see cref="ApiTokenService.CreateToken"/>.
/// Locks in the post-v10 path shape:
///
///   - User-scoped token node:  <c>{userId}/ApiToken/{hashPrefix}</c>
///   - Index node:              <c>ApiToken/{hashPrefix}</c>           (dedicated apitoken partition)
///
/// The legacy <c>User/{userId}/ApiToken/...</c> shape is gone — see Repair v10
/// (per-user partitions) and Repair v11 (rewrite apitoken.tokenPath). Any code path
/// that still writes to <c>User/{userId}/...</c> for tokens is the
/// "rogue writer" called out in the v10 postmortem.
///
/// Also verifies the produced token authenticates through the same code path the
/// MCP auth handler uses: <see cref="ApiTokenService.ValidateToken"/> reads the index
/// at <c>ApiToken/{hashPrefix}</c>, dereferences <see cref="ApiTokenIndex.TokenPath"/>
/// into the user's partition, and returns the <see cref="ApiToken"/> for principal
/// construction. If this round-trip works, MCP auth works.
/// </summary>
public class ApiTokenViaCreateNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserId = "Roland";
    private const string TokenLabel = "ci-test";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddSampleUsers();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ApiTokenService GetApiTokenService() =>
        new(MeshService, Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    [Fact]
    public async Task CreateApiToken_LandsInUserPartition_NotLegacyUserNamespace()
    {
        var (rawToken, hash, hashPrefix) = NewToken();
        var userTokenNamespace = $"{UserId}/ApiToken";

        var tokenNode = new MeshNode(hashPrefix, userTokenNamespace)
        {
            Name = $"API Token: {TokenLabel}",
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var created = await MeshService.CreateNode(tokenNode)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        created.Should().NotBeNull();
        created.Namespace.Should().Be(userTokenNamespace,
            "post-v10 token nodes must NOT use the legacy User/ prefix");
        created.Namespace.Should().NotStartWith("User/",
            "the legacy User/{id}/ApiToken/... shape is forbidden after v10");
        created.Path.Should().Be($"{UserId}/ApiToken/{hashPrefix}");
        created.MainNode.Should().Be(UserId);
        created.NodeType.Should().Be("ApiToken");
    }

    [Fact]
    public async Task CreateApiToken_PersistsHashedContent_RawTokenNeverStored()
    {
        var (rawToken, hash, hashPrefix) = NewToken();

        var tokenNode = new MeshNode(hashPrefix, $"{UserId}/ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var created = await MeshService.CreateNode(tokenNode)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var stored = created.Content as ApiToken
                     ?? DeserializeContent<ApiToken>(created);
        stored.Should().NotBeNull();
        stored!.TokenHash.Should().Be(hash);
        stored.TokenHash.Should().NotContain("mw_",
            "the raw token must never be persisted — only the hash");
        stored.UserId.Should().Be(UserId);
        stored.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task CreateApiTokenIndex_LandsInApitokenPartition_PointsAtUserPath()
    {
        var (rawToken, hash, hashPrefix) = NewToken();
        var tokenPath = $"{UserId}/ApiToken/{hashPrefix}";

        // Step 1: user-scoped token node
        await MeshService.CreateNode(new MeshNode(hashPrefix, $"{UserId}/ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Step 2: index node — global ApiToken/ namespace, content is ApiTokenIndex
        var indexNode = new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex
            {
                TokenHash = hash,
                TokenPath = tokenPath,
            },
        };

        var createdIndex = await MeshService.CreateNode(indexNode)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        createdIndex.Should().NotBeNull();
        createdIndex.Namespace.Should().Be("ApiToken");
        createdIndex.Path.Should().Be($"ApiToken/{hashPrefix}");

        var index = createdIndex.Content as ApiTokenIndex
                    ?? DeserializeContent<ApiTokenIndex>(createdIndex);
        index.Should().NotBeNull();
        index!.TokenPath.Should().Be(tokenPath,
            "the index must dereference into the user's partition (post-v10), not User/<id>/...");
        index.TokenPath.Should().NotStartWith("User/");
        index.TokenHash.Should().Be(hash);
    }

    /// <summary>
    /// End-to-end MCP auth happy path. <see cref="ApiTokenService.ValidateToken"/> is the
    /// exact lookup the MCP auth middleware (<c>ApiTokenAuthenticationHandler</c>) calls
    /// before building the principal — if it returns the token, MCP authenticates.
    /// </summary>
    [Fact]
    public async Task ValidateToken_AfterCreateNode_AuthenticatesForMcp()
    {
        var (rawToken, hash, hashPrefix) = NewToken();
        var tokenPath = $"{UserId}/ApiToken/{hashPrefix}";

        // Same writes the production CreateToken composes — but routed through
        // IMeshService.CreateNode directly, with no service in between.
        await MeshService.CreateNode(new MeshNode(hashPrefix, $"{UserId}/ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
                Roles = ["Admin"],
            },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        await MeshService.CreateNode(new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex { TokenHash = hash, TokenPath = tokenPath },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Validate via the MCP auth read path.
        var validated = await GetApiTokenService().ValidateToken(rawToken)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        validated.Should().NotBeNull("ValidateToken is the function MCP auth calls — null = 401");
        validated!.UserId.Should().Be(UserId);
        validated.UserEmail.Should().Be("rbuergi@systemorph.com");
        validated.Label.Should().Be(TokenLabel);
        validated.Roles.Should().Contain("Admin",
            "captured roles must round-trip so the MCP principal carries them as ClaimTypes.Role");
        validated.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateToken_RevokedAfterCreateNode_McpRejects()
    {
        var (rawToken, hash, hashPrefix) = NewToken();
        var tokenPath = $"{UserId}/ApiToken/{hashPrefix}";

        var created = await MeshService.CreateNode(new MeshNode(hashPrefix, $"{UserId}/ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
                IsRevoked = true,
            },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        await MeshService.CreateNode(new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex { TokenHash = hash, TokenPath = tokenPath },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var result = await GetApiTokenService().ValidateToken(rawToken)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        result.Should().BeNull("revoked tokens must not authenticate, regardless of how they were written");
    }

    [Fact]
    public async Task ValidateToken_HashMismatchOnIndex_McpRejects()
    {
        // Belt-and-braces: even if the index points at a token whose hash differs
        // (corruption / tampering), validation must fail.
        var (rawToken, hash, hashPrefix) = NewToken();
        var tokenPath = $"{UserId}/ApiToken/{hashPrefix}";
        const string tamperedHash = "0000000000000000000000000000000000000000000000000000000000000000";

        await MeshService.CreateNode(new MeshNode(hashPrefix, $"{UserId}/ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            MainNode = UserId,
            Content = new ApiToken
            {
                TokenHash = tamperedHash,
                UserId = UserId,
                UserName = "Roland",
                UserEmail = "rbuergi@systemorph.com",
                Label = TokenLabel,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        await MeshService.CreateNode(new MeshNode(hashPrefix, "ApiToken")
        {
            NodeType = "ApiToken",
            State = MeshNodeState.Active,
            Content = new ApiTokenIndex { TokenHash = hash, TokenPath = tokenPath },
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var result = await GetApiTokenService().ValidateToken(rawToken)
            .FirstAsync().ToTask(TestContext.Current.CancellationToken);

        result.Should().BeNull("a hash mismatch between index and token must reject — defence in depth");
    }

    private static (string rawToken, string hash, string hashPrefix) NewToken()
    {
        var rawBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var rawToken = "mw_" + Convert.ToBase64String(rawBytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = ApiTokenService.HashToken(rawToken);
        var hashPrefix = hash[..12];
        return (rawToken, hash, hashPrefix);
    }

    private T? DeserializeContent<T>(MeshNode node)
    {
        if (node.Content is System.Text.Json.JsonElement je)
            return System.Text.Json.JsonSerializer.Deserialize<T>(
                je.GetRawText(), Mesh.JsonSerializerOptions);
        return default;
    }
}

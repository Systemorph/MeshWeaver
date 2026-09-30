using System;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚦 The token hot path (Doc/Architecture/TokenValidationHotPath): authenticating a request must
/// not depend on a hub hop being answered. On memex, 2026-09-30 05:23Z, a validation forwarded to
/// <c>ApiToken/342abeed8e6d</c> on a replica Ready for 40 minutes was never handled; every MCP call
/// with that token answered 503 for five minutes. Measured here on a real mesh: the SHARED verdict
/// over the authoritative store validates a real token, the middleware's entry point reaches the
/// same verdict and remembers it, and an unknown token is a DEFINITIVE "not found" — the answer
/// the readiness canary depends on.
/// </summary>
public class TokenValidationHotPathTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TokenUserId = "hotpath-user";

    /// <summary>A real token validates straight from the store, and the middleware remembers it.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RealToken_ValidatesFromTheStore_AndIsCached()
    {
        var rawToken = await MintToken();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        var direct = await ApiTokenVerdict.Decide(rawToken, p => storage.Read(p, Mesh.JsonSerializerOptions), Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the direct verdict always emits exactly one answer", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(direct.Success, $"a real token must validate from the store (error: {direct.Error}, unavailable: {direct.IsUnavailable})");
        Assert.Equal(TokenUserId, direct.UserId);

        var cache = new ValidatedTokenCache();
        var viaMiddleware = await UserContextMiddleware.ValidateToken(rawToken, Mesh, cache)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the middleware's entry point must answer", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(viaMiddleware);
        Assert.True(viaMiddleware.Success);
        Assert.NotNull(cache.TryGet(ValidateTokenRequest.HashToken(rawToken), DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A token that does not exist is a DEFINITIVE "not found" from the store — never UNAVAILABLE.
    /// The readiness canary reads exactly this answer as "the path works"; if absence surfaced as
    /// UNAVAILABLE, every healthy replica would report not-ready.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task UnknownToken_IsADefinitiveNotFound_NotUnavailable()
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var verdict = await ApiTokenVerdict.Decide(
                ValidateTokenRequest.TokenPrefix + "readiness-canary-" + Guid.NewGuid().ToString("N"),
                p => storage.Read(p, Mesh.JsonSerializerOptions), Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the direct verdict always emits exactly one answer", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(verdict.Success);
        Assert.False(verdict.IsUnavailable, $"absence must be definitive, not a fault (error: {verdict.Error})");
        Assert.Equal("Token not found", verdict.Error);
    }

    private async Task<string> MintToken()
    {
        var rawToken = $"mw_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];
        await NodeFactory.CreateNode(new MeshNode(hashPrefix, $"User/{TokenUserId}/_Api")
            {
                Name = "Hot Path Token",
                NodeType = ApiTokenNodeType.NodeType,
                MainNode = $"User/{TokenUserId}",
                Content = new ApiToken
                {
                    UserId = TokenUserId,
                    UserName = TokenUserId,
                    UserEmail = "hotpath@meshweaver.io",
                    TokenHash = hash,
                    Label = "Hot Path Token",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            })
            .Should().Emit("the token record must exist", cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(new MeshNode(hashPrefix, ApiTokenNodeType.NodeType)
            {
                Name = "Hot Path Token Index",
                NodeType = ApiTokenNodeType.NodeType,
                Content = new ApiTokenIndex { TokenHash = hash, TokenPath = $"User/{TokenUserId}/_Api/{hashPrefix}" },
            })
            .Should().Emit("the index at ApiToken/{hashPrefix} must exist", cancellationToken: TestContext.Current.CancellationToken);
        return rawToken;
    }
}

/// <summary>
/// The verdict and the cache, pure: every outcome of <see cref="ApiTokenVerdict.Decide"/> over a
/// fake read, and the cache's three rules (successes only, TTL, bound).
/// </summary>
public class TokenVerdictAndCacheTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private const string Raw = "mw_0123456789abcdef0123456789abcdef";
    private static readonly string Hash = ValidateTokenRequest.HashToken(Raw);

    private static Func<string, IObservable<MeshNode?>> Store(ApiToken? token)
        => path => Observable.Return<MeshNode?>(path switch
        {
            _ when path == ApiTokenVerdict.IndexPath(Hash) => new MeshNode(Hash[..12], "ApiToken")
            {
                Content = new ApiTokenIndex { TokenHash = Hash, TokenPath = "User/u/_Api/t" },
            },
            "User/u/_Api/t" when token is not null => new MeshNode("t", "User/u/_Api") { Content = token },
            _ => null,
        });

    private static ApiToken Token(Func<ApiToken, ApiToken>? change = null)
    {
        var token = new ApiToken { TokenHash = Hash, UserId = "u", UserName = "U", UserEmail = "u@x.io", CreatedAt = DateTimeOffset.UtcNow };
        return change is null ? token : change(token);
    }

    private static async Task<ValidateTokenResponse> Decide(Func<string, IObservable<MeshNode?>> read)
        => await ApiTokenVerdict.Decide(Raw, read, Options)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the verdict always emits exactly one answer", cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>A live token: success with the owner's identity.</summary>
    [Fact]
    public async Task LiveToken_Succeeds() => Assert.Equal("u", (await Decide(Store(Token()))).UserId);

    /// <summary>Each definitive negative stays definitive (never UNAVAILABLE).</summary>
    [Fact]
    public async Task DefinitiveNegatives_AreFails()
    {
        foreach (var (read, expected) in new (Func<string, IObservable<MeshNode?>>, string)[]
                 {
                     (_ => Observable.Return<MeshNode?>(null), "Token not found"),
                     (Store(Token(t => t with { IsRevoked = true })), "Token revoked"),
                     (Store(Token(t => t with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) })), "Token expired"),
                     (Store(Token(t => t with { TokenHash = "other" })), "Invalid token"),
                 })
        {
            var verdict = await Decide(read);
            Assert.False(verdict.Success);
            Assert.False(verdict.IsUnavailable, expected);
            Assert.Equal(expected, verdict.Error);
        }
    }

    /// <summary>A read that FAULTS reached no verdict — UNAVAILABLE, never "invalid".</summary>
    [Fact]
    public async Task FaultedRead_IsUnavailable()
        => Assert.True((await Decide(_ => Observable.Throw<MeshNode?>(new TimeoutException("store")))).IsUnavailable);

    /// <summary>A read that NEVER answers becomes UNAVAILABLE at the bound — never a hang.</summary>
    [Fact]
    public async Task UnansweredRead_IsUnavailable_AtTheBound()
        => Assert.True((await Decide(_ => Observable.Never<MeshNode?>())).IsUnavailable);

    /// <summary>The cache remembers a success, and only until its TTL.</summary>
    [Fact]
    public void Cache_RemembersSuccesses_UntilTheTtl()
    {
        var cache = new ValidatedTokenCache();
        var now = DateTimeOffset.UtcNow;
        cache.Put("h", ValidateTokenResponse.Ok("u", "U", "u@x.io"), now);
        Assert.NotNull(cache.TryGet("h", now + TimeSpan.FromSeconds(1)));
        Assert.Null(cache.TryGet("h", now + ValidatedTokenCache.Ttl + TimeSpan.FromSeconds(1)));
    }

    /// <summary>A negative or unavailable verdict is never remembered.</summary>
    [Fact]
    public void Cache_NeverRemembersFailures()
    {
        var cache = new ValidatedTokenCache();
        var now = DateTimeOffset.UtcNow;
        cache.Put("a", ValidateTokenResponse.Fail("Token not found"), now);
        cache.Put("b", ValidateTokenResponse.Unavailable("store"), now);
        Assert.Equal(0, cache.Count);
    }

    /// <summary>The cache stays within its capacity.</summary>
    [Fact]
    public void Cache_IsBounded()
    {
        var cache = new ValidatedTokenCache();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < ValidatedTokenCache.Capacity + 50; i++)
            cache.Put($"h{i}", ValidateTokenResponse.Ok("u", "U", "u@x.io"), now + TimeSpan.FromMilliseconds(i));
        Assert.True(cache.Count <= ValidatedTokenCache.Capacity);
        Assert.NotNull(cache.TryGet($"h{ValidatedTokenCache.Capacity + 49}", now)); // the newest entry survives the bound
    }
}

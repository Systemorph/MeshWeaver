using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Regression for the 2026-05-01 distributed-mode permission denial.
/// Every API-token request was denied even when the token's
/// <see cref="ApiToken.Roles"/> in the database contained "Admin",
/// because <see cref="ApiTokenAuthenticationHandler"/> built the
/// principal's claim list from the token's user/email/label fields but
/// never copied <see cref="ApiToken.Roles"/> across as
/// <see cref="ClaimTypes.Role"/> claims. UserContextMiddleware then
/// stamped an empty <c>AccessContext.Roles</c>, and the
/// SecurityService claim-based path couldn't grant.
/// </summary>
public class ApiTokenAuthenticationHandlerTests
{
    [Fact]
    public void BuildClaims_TokenWithRoles_StampsEachRoleAsRoleClaim()
    {
        var token = new ApiToken
        {
            UserId = "alice",
            UserName = "Alice",
            UserEmail = "alice@example.com",
            Label = "Test Token",
            TokenHash = "deadbeef",
            Roles = ["Admin", "Editor"],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Admin",
            "every entry in ApiToken.Roles must mint a ClaimTypes.Role claim");
        claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Editor",
            "multiple roles must each become a separate Role claim");
    }

    [Fact]
    public void BuildClaims_TokenWithoutRoles_HasNoRoleClaims()
    {
        var token = new ApiToken
        {
            UserId = "bob",
            UserName = "Bob",
            UserEmail = "bob@example.com",
            Label = "No-roles token",
            TokenHash = "deadbeef",
            Roles = [],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().NotContain(c => c.Type == ClaimTypes.Role,
            "an empty Roles list must not introduce any Role claims");
    }

    [Fact]
    public void BuildClaims_AlwaysIncludesIdentityClaims()
    {
        var token = new ApiToken
        {
            UserId = "carol",
            UserName = "Carol",
            UserEmail = "carol@example.com",
            Label = "Identity test",
            TokenHash = "deadbeef",
            Roles = [],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Should().Contain(c => c.Type == "preferred_username" && c.Value == "carol");
        claims.Should().Contain(c => c.Type == ClaimTypes.Name && c.Value == "Carol");
        claims.Should().Contain(c => c.Type == ClaimTypes.Email && c.Value == "carol@example.com");
        claims.Should().Contain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "carol");
        claims.Should().Contain(c => c.Type == "token_label" && c.Value == "Identity test");
    }

    [Fact]
    public void BuildClaims_DropsEmptyRoleEntries()
    {
        var token = new ApiToken
        {
            UserId = "dave",
            UserName = "Dave",
            UserEmail = "dave@example.com",
            Label = "Empty-role test",
            TokenHash = "deadbeef",
            Roles = ["Admin", "", "Viewer"],
        };

        var claims = ApiTokenAuthenticationHandler.BuildClaims(token);

        claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "Admin", "Viewer" },
                System.Text.Json.JsonSerializerOptions.Default,
                because: "blank role entries must be silently dropped, never stamped as empty Role claims");
    }
}

/// <summary>
/// Issue #637 — "cannot validate right now" must NEVER be answered as "invalid".
///
/// <para>During a silo degradation the validation-side storage read stalls and trips
/// <see cref="ApiTokenService.ValidationReadTimeout"/>. The old code collapsed that timeout into
/// the same <c>null</c> a definitively-unknown token produces, so
/// <see cref="ApiTokenAuthenticationHandler"/> answered <c>401 Invalid or expired API token</c> —
/// indistinguishable from a forged token. Clients holding a perfectly good token discarded it and
/// forced a re-auth instead of retrying.</para>
///
/// <para>The fix is a tri-state at the service seam (<see cref="TokenValidationStatus"/>):
/// Valid / Invalid (definitive → 401) / Unavailable (no verdict → retryable). These tests pin
/// BOTH legs: a stalled read yields 503 + Retry-After, and a definitive miss still yields 401.
/// The <c>Fail</c>-always-401 trap is real — <see cref="AuthenticateResult.Fail(string)"/> goes
/// through the default challenge, so the 503 can only come from the
/// <c>HandleChallengeAsync</c> override; asserting on <c>AuthenticateResult</c> alone would pass
/// while production still returned 401.</para>
/// </summary>
public class ApiTokenValidationUnavailableTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Short enough that the stall assertions finish well inside the 30 s method timeout, long
    /// enough that a healthy read (the definitive-miss leg) resolves on its first attempt.
    /// Prod default is 8 s. NOT a tuned bound — the stalled leg never completes at ALL, so any
    /// finite window produces the same verdict.
    /// </summary>
    private static readonly TimeSpan ShortValidationWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>A well-formed token that was never minted — its index row genuinely does not exist.</summary>
    private const string UnknownButWellFormedToken = ValidateTokenRequest.TokenPrefix + "never-minted-abcdefghijklmnop";

    private ApiTokenService CreateService(IStorageAdapter storage) =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            storage,
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>())
        {
            ValidationReadTimeout = ShortValidationWindow,
        };

    private IStorageAdapter RealStorage() => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>
    /// SHOULD-FAIL-IF: the validation read timeout collapses into the same "no such token" null
    /// that a definitive miss produces. Then the handler answers 401 and an API client throws its
    /// (valid) token away. The contract is 503 + Retry-After: retryable, token NOT rejected.
    /// </summary>
    [Fact]
    public async Task ValidationReadStalls_ChallengeIs503WithRetryAfter_Never401()
    {
        var service = CreateService(new StallingIndexReadStorageAdapter(RealStorage()));

        var (context, body) = await ChallengeBearer(service, UnknownButWellFormedToken);

        context.Response.StatusCode.Should().Be(
            StatusCodes.Status503ServiceUnavailable,
            "a validation read that reached NO verdict is a retryable infrastructure fault, not an auth failure — "
            + "401 here is indistinguishable from a forged token (issue #637)");
        context.Response.StatusCode.Should().NotBe(
            StatusCodes.Status401Unauthorized,
            "answering 401 is exactly the bug: clients discard a valid token and force re-authentication");
        context.Response.Headers.RetryAfter.ToString().Should().Be(
            ApiTokenAuthenticationHandler.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture),
            "a retryable answer must tell the client WHEN to retry");
        body.Should().Contain(
            "NOT rejected",
            "the body must say the token survived, so a client library does not treat this as a credential problem");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the fix over-reaches and turns every negative into a retryable 503 — then a
    /// forged/expired/unknown token would make clients retry forever instead of re-authenticating.
    /// A DEFINITIVE negative verdict must still be 401, with no Retry-After.
    /// </summary>
    [Fact]
    public async Task DefinitivelyUnknownToken_ChallengeIsStill401()
    {
        var service = CreateService(RealStorage());

        var (context, _) = await ChallengeBearer(service, UnknownButWellFormedToken);

        context.Response.StatusCode.Should().Be(
            StatusCodes.Status401Unauthorized,
            "an absent index row is a DEFINITIVE negative verdict — 401 stays 401");
        context.Response.Headers.RetryAfter.ToString().Should().BeNullOrEmpty(
            "a definitive rejection is not retryable; advertising Retry-After would invite a retry storm");
    }

    /// <summary>
    /// The seam itself: a stalled read must reach <see cref="TokenValidationStatus.Unavailable"/>,
    /// NOT <see cref="TokenValidationStatus.Invalid"/>. This is what every mirror
    /// (UserContextMiddleware, SignalR/gRPC registries, ApiTokenNodeType) branches on.
    /// </summary>
    [Fact]
    public async Task Validate_StalledRead_IsUnavailable_NotInvalid()
    {
        var service = CreateService(new StallingIndexReadStorageAdapter(RealStorage()));

        var result = await service.Validate(UnknownButWellFormedToken).Should().Emit();

        result.Status.Should().Be(
            TokenValidationStatus.Unavailable,
            "the store could not be read — no verdict about the token was reached");
        result.Token.Should().BeNull("an unavailable validation carries no token record");
    }

    /// <summary>
    /// The other seam leg: a genuinely absent row is a verdict, and must stay
    /// <see cref="TokenValidationStatus.Invalid"/> so it is answered 401.
    /// </summary>
    [Fact]
    public async Task Validate_UnknownToken_IsInvalid_NotUnavailable()
    {
        var service = CreateService(RealStorage());

        var result = await service.Validate(UnknownButWellFormedToken).Should().Emit();

        result.Status.Should().Be(
            TokenValidationStatus.Invalid,
            "an absent index row IS a verdict: the token is unknown");
    }

    /// <summary>
    /// The compat surface stays exactly as it was: <see cref="ApiTokenService.ValidateToken"/>
    /// projects BOTH negative shapes to null. Callers that need the distinction must move to
    /// <see cref="ApiTokenService.Validate"/> — this pins that no existing caller silently
    /// changed behavior when the tri-state landed.
    /// </summary>
    [Fact]
    public async Task ValidateToken_CompatProjection_StillNullForBothNegatives()
    {
        var stalled = await CreateService(new StallingIndexReadStorageAdapter(RealStorage()))
            .ValidateToken(UnknownButWellFormedToken).Should().Emit();
        var unknown = await CreateService(RealStorage())
            .ValidateToken(UnknownButWellFormedToken).Should().Emit();

        stalled.Should().BeNull("the legacy surface cannot express 'unavailable' — it stays null");
        unknown.Should().BeNull("an unknown token has always been null on the legacy surface");
    }

    /// <summary>
    /// Runs the real <see cref="ApiTokenAuthenticationHandler"/> over a real
    /// <see cref="HttpContext"/>: authenticate the Bearer token, then issue the challenge that
    /// ASP.NET Core would issue for a failed authentication, and return the resulting response.
    /// Going through <c>ChallengeAsync</c> is the whole point — <c>AuthenticateResult.Fail</c>
    /// looks identical for both negatives; only the challenge distinguishes 401 from 503.
    /// </summary>
    private static async Task<(HttpContext Context, string Body)> ChallengeBearer(
        ApiTokenService service, string rawToken)
    {
        var services = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .AddSingleton(service)
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        var body = new MemoryStream();
        context.Response.Body = body;

        var handler = new ApiTokenAuthenticationHandler(
            services.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            services.GetRequiredService<ILoggerFactory>(),
            UrlEncoder.Default,
            services);
        await handler.InitializeAsync(
            new AuthenticationScheme(
                ApiTokenAuthenticationHandler.SchemeName, null, typeof(ApiTokenAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();
        result.Succeeded.Should().BeFalse("neither leg of this helper presents a mintable token");

        await handler.ChallengeAsync(properties: null);

        return (context, Encoding.UTF8.GetString(body.ToArray()));
    }
}

/// <summary>
/// Fault injector for the ONE thing issue #637 is about: the validation-side index read never
/// completing (the silo-degradation shape). Every other operation is forwarded verbatim to the
/// real adapter, so the mesh under test behaves normally — only <c>ApiToken/{hashPrefix}</c>
/// reads hang, which is what <see cref="ApiTokenService.ValidationReadTimeout"/> exists to bound.
///
/// <para><see cref="Observable.Never{T}()"/> rather than a throw: a throw re-polls (a connection
/// blip is retried by design), while a stalled read is the deterministic way to reach the outer
/// <c>Timeout</c> exactly once — no sleeps, no races, no retry budget to reason about.</para>
/// </summary>
internal sealed class StallingIndexReadStorageAdapter(IStorageAdapter inner) : IStorageAdapter
{
    private const string ApiTokenIndexPrefix = "ApiToken/";

    /// <summary>🚨 Decorators MUST forward Changes — the interface default starves synced queries.</summary>
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => path.StartsWith(ApiTokenIndexPrefix, StringComparison.OrdinalIgnoreCase)
            ? Observable.Never<MeshNode?>()
            : inner.Read(path, options);

    /// <inheritdoc />
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => paths.Any(p => p.StartsWith(ApiTokenIndexPrefix, StringComparison.OrdinalIgnoreCase))
            ? Observable.Never<MeshNode>()
            : inner.ReadMany(paths, options);

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => inner.Write(node, options);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => inner.WriteMany(nodes, options);

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => inner.Delete(path);

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(
        string? parentPath) => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options) => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options) => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}

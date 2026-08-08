using System.Globalization;
using System.Reactive.Linq;
using System.Text;
using System.Text.Encodings.Web;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Issue #637, the identity half — "we could not find out" must NEVER be reported as
/// "we found out, and the answer is no".
///
/// <para>PR #947 fixed the API-token leg (a validation read timeout answers 503 + Retry-After
/// instead of 401). The SAME collapse survived on the identity/authorization leg, where it is
/// arguably worse because it is what a browser user sees:</para>
/// <list type="bullet">
///   <item><description><c>FindUserByEmail</c> fell back to <c>null</c> on timeout, and <c>null</c>
///     means "not onboarded" — so a storage stall bounced a fully signed-in user to the SIGN-UP
///     form. Re-signing-in cannot fix a storage stall.</description></item>
///   <item><description><c>LoadUserRoles</c> defaulted to an EMPTY role set on timeout/fault,
///     indistinguishable from "this user has no grants" — so a blip silently stripped a user's
///     privileges and every screen they opened answered "Access denied".</description></item>
/// </list>
///
/// <para>The fix is a two-shape outcome at the read itself (<see cref="IdentityReadOutcome{T}"/>):
/// Resolved (the answer, possibly a definitive "no") vs Unavailable (no answer). These tests pin
/// the CLASSIFICATION under an induced stall — not merely that "an error appears" — plus the
/// definitive legs, so the fix cannot over-reach into "everything is retryable".</para>
/// </summary>
public class IdentityReadClassificationTests
{
    /// <summary>Short enough to finish well inside the method timeout; the stalled leg never
    /// completes at all, so any finite window yields the same verdict — this is not a tuned bound.</summary>
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// SHOULD-FAIL-IF: a read that never completes is classified as a verdict. That is the whole
    /// defect — the caller then reports "no account" / "no roles" for what is a storage stall.
    /// </summary>
    [Fact]
    public async Task StalledRead_IsUnavailable_NotADefinitiveNegative()
    {
        var outcome = await IdentityRead
            .Bounded(Observable.Never<MeshNode?>(), ShortWindow, "FindUserByEmail(stalled)", logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeTrue(
            "a read that reached no verdict says NOTHING about the user — reporting it as a "
            + "negative identity verdict is exactly issue #637");
        outcome.Value.Should().BeNull("an unavailable read carries no answer");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: a faulted read silently defaults instead of surfacing. A swallow here is how
    /// the role lookup used to hand back an empty role set for a broken query layer.
    /// </summary>
    [Fact]
    public async Task FaultedRead_IsUnavailable_NotADefinitiveNegative()
    {
        var outcome = await IdentityRead
            .Bounded(
                Observable.Throw<MeshNode?>(new InvalidOperationException("query layer down")),
                ShortWindow, "FindUserByEmail(faulted)", logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeTrue(
            "a fault is not a verdict about the user either — it must be carried out, never defaulted away");
        outcome.UnavailableReason.Should().Contain("InvalidOperationException",
            "the operator needs the failing shape in the reason, not a generic banner");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the fix over-reaches and makes every negative retryable — then a genuinely
    /// unknown user could never be sent to onboarding, and a user with no grants would 503 forever.
    /// </summary>
    [Fact]
    public async Task CompletedReadWithNoMatch_IsResolvedAbsent_NotUnavailable()
    {
        var outcome = await IdentityRead
            .Bounded(Observable.Return<MeshNode?>(null), ShortWindow, "FindUserByEmail(absent)", logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeFalse(
            "an absent row IS a verdict: this user genuinely has no account — it must stay actionable");
        outcome.Value.Should().BeNull();
    }

    /// <summary>The happy leg: a completed read carries its value through unchanged.</summary>
    [Fact]
    public async Task CompletedReadWithMatch_IsResolvedWithValue()
    {
        var node = new MeshNode("alice") { NodeType = "User", Name = "Alice" };

        var outcome = await IdentityRead
            .Bounded(Observable.Return<MeshNode?>(node), ShortWindow, "FindUserByEmail(hit)", logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeFalse();
        outcome.Value!.Id.Should().Be("alice");
    }
}

/// <summary>
/// The HTTP shape of the identity-unavailable answer (issue #637). Asserting the classification
/// alone would pass while production still 302'd a signed-in user to the sign-up form — the
/// response is where the user actually experiences the bug, so it gets its own pins.
/// </summary>
public class IdentityUnavailableResponseTests
{
    /// <summary>
    /// SHOULD-FAIL-IF: an unresolvable identity is answered with anything that reads as an identity
    /// problem — a redirect to /onboarding, a 401, or a bare 500. The contract is a retryable 503
    /// carrying Retry-After, and copy that tells the user their sign-in is fine.
    /// </summary>
    [Fact]
    public async Task IdentityUnavailable_Is503WithRetryAfter_NeverARedirectOr401()
    {
        var (context, body) = await WriteUnavailable(locale: null);

        context.Response.StatusCode.Should().Be(
            StatusCodes.Status503ServiceUnavailable,
            "an identity read that reached no verdict is an availability failure — reporting it as "
            + "an identity failure sends a correctly signed-in user somewhere that cannot help them");
        context.Response.StatusCode.Should().NotBe(StatusCodes.Status401Unauthorized);
        context.Response.Headers.Location.ToString().Should().BeNullOrEmpty(
            "bouncing to /onboarding is the defect: it tells a signed-in user their account does not exist");
        context.Response.Headers.RetryAfter.ToString().Should().Be(
            ApiTokenAuthenticationHandler.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture),
            "a retryable answer must say WHEN to retry — and it shares the API-token constant so the two cannot drift");
        body.Should().Contain("signed in",
            "the copy has to say the sign-in survived, or the user does the one thing that cannot help: sign in again");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the text is hard-coded English. Every user-visible string resolves through
    /// the catalog off <see cref="AccessContext.Locale"/> — a German viewer must not be shown
    /// English. (<c>LocalizationTest</c> separately guarantees the German key exists.)
    /// </summary>
    [Fact]
    public async Task IdentityUnavailableBody_IsLocalized_OffAccessContextLocale()
    {
        var (_, english) = await WriteUnavailable("en");
        var (_, german) = await WriteUnavailable("de");

        english.Should().NotBeNullOrWhiteSpace();
        german.Should().NotBeNullOrWhiteSpace();
        german.Should().NotBe(english,
            "the body must come from the localization catalog resolved off AccessContext.Locale — "
            + "identical text for en and de means it was hard-coded");
    }

    private static async Task<(HttpContext Context, string Body)> WriteUnavailable(string? locale)
    {
        var access = new AccessService();
        access.SetContext(new AccessContext
        {
            ObjectId = "alice",
            Name = "Alice",
            Email = "alice@example.com",
            Locale = locale,
        });

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Path = "/some/page";
        var body = new MemoryStream();
        context.Response.Body = body;

        await OnboardingMiddleware.WriteIdentityUnavailable(
            context, "FindUserByEmail(alice@example.com) reached no verdict within 20s", access);

        return (context, Encoding.UTF8.GetString(body.ToArray()));
    }
}

/// <summary>
/// The API-token leg of the SAME collapse (issue #637): role enrichment used to sit behind a bare
/// <c>catch { }</c>, so a role store that could not be read authenticated the caller with a
/// SILENTLY REDUCED role set. Every later request they made was then refused "Access denied" —
/// an availability failure reported as an authorization failure, and one that looks like success
/// at the moment it happens, which is exactly why it needs pinning.
///
/// <para>Both legs use a REAL mesh and a REAL minted token, so only the reachability of the role
/// store differs between them.</para>
/// </summary>
public class ApiTokenRoleResolutionUnavailableTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// SHOULD-FAIL-IF: an unreadable role store is absorbed and the request authenticates anyway.
    /// That is the pre-fix behaviour — and it succeeds silently, so nothing downstream can tell
    /// the caller why they are suddenly being denied.
    /// </summary>
    [Fact]
    public async Task RoleStoreUnreachable_ChallengeIs503WithRetryAfter_NotAReducedPrincipal()
    {
        var rawToken = await MintValidToken();

        // The role source cannot be produced at all — the shape a portal mid-restart presents.
        // Token validation still works (it reads storage directly), so ONLY role resolution fails.
        var (context, result) = await AuthenticateAndChallenge(rawToken,
            services => services.AddSingleton<IMessageHub>(
                _ => throw new InvalidOperationException("mesh unavailable")));

        result.Succeeded.Should().BeFalse(
            "authenticating with a silently reduced role set is the defect — the caller would then "
            + "be denied everywhere with no way to tell an outage from a revoked grant");
        context.Response.StatusCode.Should().Be(
            StatusCodes.Status503ServiceUnavailable,
            "an unreadable role store reached no verdict about this caller's permissions — retryable");
        context.Response.StatusCode.Should().NotBe(StatusCodes.Status401Unauthorized,
            "the token itself was validated fine; answering 401 would send a good token to be re-minted");
        context.Response.Headers.RetryAfter.ToString().Should().Be(
            ApiTokenAuthenticationHandler.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the fix over-reaches and every token request 503s. A reachable role store —
    /// even one holding no grants for this user — must authenticate exactly as before.
    /// </summary>
    [Fact]
    public async Task RoleStoreReachable_StillAuthenticates()
    {
        var rawToken = await MintValidToken();

        var (context, result) = await AuthenticateAndChallenge(rawToken,
            services => services.AddSingleton<IMessageHub>(Mesh));

        result.Succeeded.Should().BeTrue(
            "a reachable role store is the normal path — this pins that the retryable branch did "
            + "not swallow the happy path");
        context.Response.StatusCode.Should().NotBe(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The seam the handler branches on, exercised against the real workspace: a budget the read
    /// cannot possibly meet must classify Unavailable rather than hand back an empty role set.
    /// </summary>
    [Fact]
    public async Task LoadDbRoles_WithUnmeetableBudget_IsUnavailable_NotEmptyRoles()
    {
        var services = new ServiceCollection().AddSingleton<IMessageHub>(Mesh).BuildServiceProvider();

        var outcome = await UserRoleResolver.LoadDbRolesAsync(services, "someuser", TimeSpan.Zero);

        outcome.IsUnavailable.Should().BeTrue(
            "a read that could not complete says nothing about the user's grants — an empty set here "
            + "is indistinguishable from 'this user has none' and silently strips their access");
    }

    /// <summary>The same call with a real budget resolves — the no-over-reach twin of the above.</summary>
    [Fact]
    public async Task LoadDbRoles_WithRealBudget_ResolvesEmptyForUserWithNoGrants()
    {
        var services = new ServiceCollection().AddSingleton<IMessageHub>(Mesh).BuildServiceProvider();

        var outcome = await UserRoleResolver.LoadDbRolesAsync(
            services, $"nobody{Guid.NewGuid():N}"[..16]);

        outcome.IsUnavailable.Should().BeFalse("the query converged — that IS a verdict");
        outcome.Value.Should().BeEmpty();
    }

    private ApiTokenService CreateTokenService() =>
        new(Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    /// <summary>Mints a real token and waits until it actually validates (absorbs read-side index lag).</summary>
    private async Task<string> MintValidToken()
    {
        var service = CreateTokenService();
        var created = await service.CreateToken(
            "roleuser", "Role User", "roleuser@example.com", "Role test").Should().Emit();

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => service.ValidateToken(created.RawToken).Take(1))
            .Should().Match(v => v is not null);

        return created.RawToken;
    }

    /// <summary>
    /// Runs the real handler over a real <see cref="HttpContext"/>: authenticate, then issue the
    /// challenge ASP.NET would issue for a failed authentication. The challenge is where 401 and
    /// 503 diverge — <c>AuthenticateResult.Fail</c> looks identical for both.
    /// </summary>
    private async Task<(HttpContext Context, AuthenticateResult Result)> AuthenticateAndChallenge(
        string rawToken, Action<IServiceCollection> configure)
    {
        IServiceCollection collection = new ServiceCollection();
        collection.AddOptions().AddLogging();
        collection.AddSingleton(CreateTokenService());
        configure(collection);
        var services = collection.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = $"Bearer {rawToken}";
        context.Response.Body = new MemoryStream();

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
        if (!result.Succeeded)
            await handler.ChallengeAsync(properties: null);

        return (context, result);
    }
}

/// <summary>
/// The definitive legs against a REAL mesh — the guard against over-reach. A converging lookup for
/// a user who genuinely does not exist must stay a verdict, so onboarding still works; likewise a
/// user with no grants must resolve to an empty role set, not to "unavailable".
/// </summary>
public class IdentityReadDefinitiveLegTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public async Task UnknownEmail_ResolvesToDefinitiveAbsence_SoOnboardingStillWorks()
    {
        var email = $"nobody-{Guid.NewGuid():N}@nowhere.invalid";

        var outcome = await OnboardingMiddleware
            .FindUserByEmail(Mesh.GetWorkspace(), email, logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeFalse(
            "the lookup CONVERGED — an empty snapshot for an unknown email is a verdict, and the "
            + "user must still be sent to onboarding rather than shown a retryable 503");
        outcome.Value.Should().BeNull("no User node exists for this email");
    }

    [Fact]
    public async Task UserWithNoGrants_ResolvesToEmptyRoles_NotUnavailable()
    {
        var username = $"nobody{Guid.NewGuid():N}"[..16];

        var outcome = await OnboardingMiddleware
            .LoadUserRoles(Mesh.GetWorkspace(), username, logger: null)
            .Should().Emit();

        outcome.IsUnavailable.Should().BeFalse(
            "a converged role query that found no AccessAssignment is a verdict — only a stalled or "
            + "faulted read is unavailable");
        outcome.Value.Should().BeEmpty("this user has no grants");
    }
}

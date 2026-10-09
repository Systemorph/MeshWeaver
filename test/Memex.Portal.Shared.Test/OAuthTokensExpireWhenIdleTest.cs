using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// An OAuth-minted token (label <c>OAuth: {client_id}</c>) expires once it has gone unused for
/// longer than <see cref="OAuthTokenLifetime.IdleLifetime"/>. Every validator refuses it: the HTTP
/// authentication handler's <see cref="ApiTokenService.Validate"/> and the shared
/// <see cref="ApiTokenVerdict"/>, which the auth middleware's direct-store path and the
/// <c>ApiToken/{hashPrefix}</c> hub run. The expiry sweep deletes it. A token a person minted by
/// hand is never subject to the rule, and no manual surface may mint into the reserved label.
///
/// <para><b>Why (Plugins#2772).</b> Supersession keys on <c>client_id</c>, which a loopback client
/// derives from its redirect URI, so every re-registration on a new port opened a fresh slot and
/// left the previous one-year token live with nothing able to reach it.</para>
///
/// <para><b>Controls.</b> Each refusal is paired with the case on the other side of the line, so
/// no constant verdict passes the class: a recently used OAuth token stays valid (the bound runs
/// from the LAST use), and a hand-minted token far older than the bound stays valid (the rule is
/// scoped by label, not by age). Every fixture is minted and its own start-of-mint sweep has
/// finished BEFORE any row is aged, so the one sweep the test then triggers is the only one that
/// can delete the idle row.</para>
/// </summary>
public class OAuthTokensExpireWhenIdleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string User = "idleoauthuser";

    private CapturingLogger<ApiTokenService> Log { get; } = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private ApiTokenService Service() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Storage,
        Log);

    private static readonly TimeSpan Days31 = TimeSpan.FromDays(31);
    private static readonly TimeSpan Days29 = TimeSpan.FromDays(29);
    private static string Namespace => $"{User}/ApiToken";

    private Task<T> Within<T>(IObservable<T> source, string because = "") =>
        source.Should().Within(TestTimeouts.Convergence).Emit(because, TestContext.Current.CancellationToken);

    /// <summary>Rewrites a stored token row so it was created <paramref name="createdAgo"/> and last
    /// used <paramref name="lastUsedAgo"/> before now.</summary>
    private async Task Age(MeshNode minted, TimeSpan createdAgo, TimeSpan? lastUsedAgo)
    {
        var now = DateTimeOffset.UtcNow;
        var token = minted.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)!;
        await Within(Storage.Write(minted with
        {
            Content = token with
            {
                CreatedAt = now - createdAgo,
                LastUsedAt = lastUsedAgo is { } ago ? now - ago : null,
            },
        }, Mesh.JsonSerializerOptions));
    }

    private Task<ValidateTokenResponse> SharedVerdict(string raw) =>
        Within(ApiTokenVerdict.Decide(raw, path => Storage.Read(path, Mesh.JsonSerializerOptions), Mesh.JsonSerializerOptions));

    [Fact]
    public void TheIdleRule_IsScopedToOAuthLabels_AndMeasuredFromLastUse()
    {
        var now = DateTimeOffset.UtcNow;
        var oauth = new ApiToken { Label = OAuthTokenLifetime.LabelFor("abc"), CreatedAt = now - TimeSpan.FromDays(300) };

        OAuthTokenLifetime.IsIdle(oauth with { LastUsedAt = now - Days31 }, now).Should().BeTrue();
        OAuthTokenLifetime.IsIdle(oauth with { LastUsedAt = null }, now)
            .Should().BeTrue("a never-used OAuth token is measured from its creation");
        OAuthTokenLifetime.IsIdle(oauth with { LastUsedAt = now - Days29 }, now)
            .Should().BeFalse("recent use keeps an old OAuth token alive");
        OAuthTokenLifetime.IsIdle(oauth with { CreatedAt = now - Days29, LastUsedAt = null }, now)
            .Should().BeFalse("a fresh, not-yet-used OAuth token is inside the bound");
        OAuthTokenLifetime.IsIdle(new ApiToken { Label = "My laptop", CreatedAt = now - TimeSpan.FromDays(400) }, now)
            .Should().BeFalse("a hand-minted token is never subject to the idle rule");
    }

    [Theory]
    [InlineData("OAuth: my-client")]
    [InlineData("oauth: lower")]
    [InlineData("  OAuth:spaced")]
    public async Task AManualMint_MayNotWearTheReservedOAuthLabel(string label)
    {
        OAuthTokenLifetime.IsReservedLabel(label).Should().BeTrue();
        var refused = await Service().CreateToken(User, User, $"{User}@example.com", label)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().BeOfType<InvalidOperationException>(
            "a hand-minted token must not opt itself into the OAuth idle rule by its name");
        OAuthTokenLifetime.IsReservedLabel("My OAuth laptop").Should().BeFalse("only the prefix is reserved");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnIdleOAuthToken_IsRefusedByEveryValidator_AndSwept_WhileLiveAndHandMintedTokensSurvive()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var service = Service();
        var sweeps = service.ExpirySweepCompleted.Where(ns => ns == Namespace).Replay();
        using var connection = sweeps.Connect();

        // 1. Mint every fixture, and let each mint's own sweep finish, BEFORE anything is aged.
        var idle = await Within(service.CreateOAuthToken(User, User, $"{User}@example.com", "idle-client", null));
        var active = await Within(service.CreateOAuthToken(User, User, $"{User}@example.com", "active-client", null));
        var manual = await Within(service.CreateToken(User, User, $"{User}@example.com", "My laptop"));
        await Within(sweeps.Take(3).LastAsync(), "the three mint-time sweeps finish before any row is aged");

        // 2. Age them: idle unused for 31 days, active used 29 days ago, manual 400 days old.
        await Age(idle.Node, TimeSpan.FromDays(90), Days31);
        await Age(active.Node, TimeSpan.FromDays(90), Days29);
        await Age(manual.Node, TimeSpan.FromDays(400), null);

        // 3. The HTTP authentication handler's validator.
        (await Within(service.Validate(idle.RawToken))).Status.Should().Be(TokenValidationStatus.Invalid,
            "an OAuth token unused for 31 days has expired");
        Log.Lines(LogLevel.Warning).Should().Contain(l => l.Contains("oauth-idle-expired"),
            "the refusal names its stage, so a 401 is diagnosable");
        (await Within(service.Validate(active.RawToken))).Status.Should().Be(TokenValidationStatus.Valid,
            "negative control: an OAuth token used 29 days ago is still live");
        (await Within(service.Validate(manual.RawToken))).Status.Should().Be(TokenValidationStatus.Valid,
            "negative control: a hand-minted token without expiry is never idle-expired");

        // 4. The shared verdict — what the auth middleware's direct-store path and the ApiToken hub run.
        var idleVerdict = await SharedVerdict(idle.RawToken);
        idleVerdict.Success.Should().BeFalse("the middleware and the hub refuse the idle token too");
        idleVerdict.IsUnavailable.Should().BeFalse("it is a definitive refusal, not a store hiccup");
        (await SharedVerdict(active.RawToken)).Success.Should().BeTrue("negative control on the shared verdict");
        (await SharedVerdict(manual.RawToken)).Success.Should().BeTrue("negative control on the shared verdict");

        // 5. Exactly one more sweep — triggered by the next mint — deletes the idle row and only it.
        var nextSweep = Within(sweeps.Skip(3).Take(1), "the trigger mint's sweep completes");
        await Within(service.CreateToken(User, User, $"{User}@example.com", "trigger sweep"));
        await nextSweep;

        var remaining = (await Within(Storage.ListChildPaths(Namespace).Take(1))).NodePaths.ToArray();
        remaining.Should().NotContain(idle.Node.Path, "the next mint's sweep deletes the idle OAuth token");
        remaining.Should().Contain(active.Node.Path, "the live OAuth token is kept");
        remaining.Should().Contain(manual.Node.Path, "the hand-minted token is kept");
    }
}

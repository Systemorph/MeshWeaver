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
/// longer than <see cref="ApiTokenService.OAuthIdleLifetime"/>: validation refuses it and the
/// expiry sweep deletes it. A token a person minted by hand is never touched by the idle rule.
///
/// <para><b>Why (Plugins#2772).</b> Supersession keys on <c>client_id</c>, which a loopback client
/// derives from its redirect URI, so every re-registration on a new port opened a fresh slot and
/// left the previous one-year token live with nothing able to reach it. Measured: one user held ten
/// OAuth-minted tokens over seven distinct client ids, the oldest unused for three months.</para>
///
/// <para><b>Controls.</b> Each refusal is paired with the case on the other side of the line, so
/// no constant verdict passes the class: a recently used OAuth token stays valid (the bound is
/// measured from the LAST use, not from creation), and a hand-minted token far older than the bound
/// stays valid (the rule is scoped by label, not by age).</para>
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

    /// <summary>Mints a token, then rewrites its stored row so it was created
    /// <paramref name="createdAgo"/> and last used <paramref name="lastUsedAgo"/> before now.</summary>
    private async Task<(string Raw, string Path)> MintAged(
        ApiTokenService service, string label, TimeSpan createdAgo, TimeSpan? lastUsedAgo)
    {
        var minted = await service.CreateToken(User, User, $"{User}@example.com", label)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var token = minted.Node.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)!;
        var aged = minted.Node with
        {
            Content = token with
            {
                CreatedAt = now - createdAgo,
                LastUsedAt = lastUsedAgo is { } ago ? now - ago : null,
            },
        };
        await Storage.Write(aged, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        return (minted.RawToken, minted.Node.Path);
    }

    private async Task<TokenValidationStatus> Validate(ApiTokenService service, string raw) =>
        (await service.Validate(raw)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken)).Status;

    [Fact]
    public void TheIdleRule_IsScopedToOAuthLabels_AndMeasuredFromLastUse()
    {
        var now = DateTimeOffset.UtcNow;
        var oauth = new ApiToken { Label = ApiTokenService.OAuthLabelPrefix + "abc", CreatedAt = now - TimeSpan.FromDays(300) };

        ApiTokenService.IsIdleOAuthToken(oauth with { LastUsedAt = now - Days31 }, now).Should().BeTrue();
        ApiTokenService.IsIdleOAuthToken(oauth with { LastUsedAt = null }, now)
            .Should().BeTrue("a never-used OAuth token is measured from its creation");
        ApiTokenService.IsIdleOAuthToken(oauth with { LastUsedAt = now - Days29 }, now)
            .Should().BeFalse("recent use keeps an old OAuth token alive");
        ApiTokenService.IsIdleOAuthToken(oauth with { CreatedAt = now - Days29, LastUsedAt = null }, now)
            .Should().BeFalse("a fresh, not-yet-used OAuth token is inside the bound");
        ApiTokenService.IsIdleOAuthToken(
                new ApiToken { Label = "My laptop", CreatedAt = now - TimeSpan.FromDays(400) }, now)
            .Should().BeFalse("a hand-minted token is never subject to the idle rule");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnIdleOAuthToken_IsRefused_AndSwept_WhileLiveAndHandMintedTokensSurvive()
    {
        var service = Service();
        var idle = await MintAged(service, ApiTokenService.OAuthLabelPrefix + "idle-client", TimeSpan.FromDays(90), Days31);
        var active = await MintAged(service, ApiTokenService.OAuthLabelPrefix + "active-client", TimeSpan.FromDays(90), Days29);
        var manual = await MintAged(service, "My laptop", TimeSpan.FromDays(400), null);

        (await Validate(service, idle.Raw)).Should().Be(TokenValidationStatus.Invalid,
            "an OAuth token unused for 31 days has expired");
        (await Validate(service, active.Raw)).Should().Be(TokenValidationStatus.Valid,
            "negative control: an OAuth token used 29 days ago is still live");
        (await Validate(service, manual.Raw)).Should().Be(TokenValidationStatus.Valid,
            "negative control: a hand-minted token without expiry is never idle-expired");
        Log.Lines(LogLevel.Warning).Should().Contain(l => l.Contains("oauth-idle-expired"),
            "the refusal names its stage, so a 401 is diagnosable");

        // The sweep runs on the next mint for this user. Wait on the CONDITION — the idle row gone
        // from the authoritative store — never on a delay.
        await service.CreateToken(User, User, $"{User}@example.com", "trigger sweep")
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        var remaining = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.ListChildPaths($"{User}/ApiToken").Take(1))
            .Select(listing => listing.NodePaths.ToArray())
            .Where(paths => !paths.Contains(idle.Path))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the expiry sweep deletes the idle OAuth token", TestContext.Current.CancellationToken);
                remaining.Should().Contain(active.Path, "the live OAuth token is kept");
        remaining.Should().Contain(manual.Path, "the hand-minted token is kept");
    }
}

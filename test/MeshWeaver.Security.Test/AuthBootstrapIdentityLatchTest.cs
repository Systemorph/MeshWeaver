using System;
using System.Security.Cryptography;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Security.Test;

/// <summary>
/// #1790, tier 1 — the AUTH BOOTSTRAP. <c>UserContextMiddleware.ValidateTokenViaHub</c> deliberately
/// runs as System: it is what turns a raw Bearer token into a user identity, so by construction the
/// caller is unauthenticated, and without <c>Permission.All</c> the never-null post guard
/// fail-closes and every authenticated user sees a blank portal (prod 2026-06-18, issue #637). The
/// impersonation is correct. What was not correct is that it never came back.
///
/// <para><b>The defect.</b> The validation was composed as
/// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; hub.Observe(…))</c>. Impersonation
/// is an <c>AsyncLocal</c> store/restore pair; Rx runs the resource factory on the SUBSCRIBING
/// thread and disposes the resource when the inner observable TERMINATES — here, when the ApiToken
/// hub answers, on a hub thread. The subscribing thread is the ASP.NET REQUEST thread
/// (<c>InvokeAsync</c> bridges this stream with <c>.ToTask()</c>), so it was left holding
/// <c>system-security</c> for the remainder of the request.</para>
///
/// <para><b>Why that is an escalation and not an untidiness.</b> Immediately after the bridge,
/// <c>InvokeAsync</c> reads <c>userService.Context</c> into <c>existing</c> and reuses it when
/// <c>existing.Email == userContext.Email</c>. The latched System context has a null Email, so a
/// token record carrying no email matched it — and the request proceeded with the System context
/// set as the caller's own. The blast radius is every request that presents a Bearer token.</para>
///
/// <para><b>Why the assertion is the NEGATIVE one.</b> Validation succeeds either way; the only
/// observable difference is what the calling thread is left holding. So that is what is asserted, at
/// the one moment it is observable — after <c>.ToTask()</c> has subscribed (which is when the scope
/// is opened) and before the response arrives (which is when the old shape would have disposed it,
/// on someone else's thread).</para>
///
/// <para><b>Non-vacuity.</b> The response is asserted SUCCESSFUL in the same test: the token node is
/// readable only under the System scope, so a "fix" that simply stopped impersonating would turn the
/// verdict into a failure here rather than pass quietly. Reverting
/// <c>ImpersonationScopeExtensions</c> to its <c>Observable.Using</c> form turns the identity
/// assertion red on the first run.</para>
/// </summary>
public class AuthBootstrapIdentityLatchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

    private const string TokenOwner = "token-owner";
    private const string SystemId = "system-security";

    private static readonly AccessContext Caller = new()
    {
        ObjectId = "alice",
        Name = "Alice",
        Email = "alice@example.com",
    };

    [Fact]
    public async Task ValidatingABearerToken_LeavesTheRequestThreadsOwnIdentityBehind()
    {
        var rawToken = await CreateApiToken();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        string? identityRightAfterSubscribe;
        ValidateTokenResponse? response;

        using (access.SwitchAccessContext(Caller))
        {
            access.Context?.ObjectId.Should().Be("alice",
                "the premise: this thread carries a real caller BEFORE the bootstrap runs, exactly "
                + "as an ASP.NET request thread does");

            // ToTask() subscribes SYNCHRONOUSLY on this thread and returns — which is precisely the
            // shape UserContextMiddleware.InvokeAsync uses, and the moment the scope is opened.
            var validation = UserContextMiddleware
                .ValidateTokenViaHub(rawToken, Mesh)
                .FirstAsync()
                .Timeout(StepTimeout)
                .Await(TestContext.Current.CancellationToken);

            // ── The assertion. Read BEFORE awaiting: the response has not arrived, so under the old
            // shape nothing has disposed the scope and this thread is still impersonated.
            identityRightAfterSubscribe = access.Context?.ObjectId;

            response = await validation;
        }

        identityRightAfterSubscribe.Should().NotBe(SystemId,
            "the auth bootstrap's System scope must not outlive the Subscribe that opened it. "
            + "Observing 'system-security' here means the ASP.NET request thread runs the REST of "
            + "the request with Permission.All — including InvokeAsync's `existing.Email == "
            + "userContext.Email` reuse branch, which then adopts the System context as the "
            + "caller's own (#1790)");
        identityRightAfterSubscribe.Should().Be("alice",
            "and it must be the caller's own identity that is handed back — not merely 'not System'");

        response.Should().NotBeNull("non-vacuity: the validation must have produced a verdict");
        response!.Success.Should().BeTrue(
            "non-vacuity, and the half that matters: the token node is readable only under the "
            + "System scope, so a green identity assertion with a FAILED verdict would mean the "
            + "impersonation had simply been dropped — trading an escalation for a portal outage. "
            + "Error was: " + (response.Error ?? "(none)"));
        response.UserId.Should().Be(TokenOwner);

        access.Context?.ObjectId.Should().Be("alice",
            "and the enclosing scope still restores normally afterwards");
    }

    /// <summary>
    /// Mints an ApiToken node at <c>ApiToken/{hashPrefix}</c> — the address
    /// <see cref="UserContextMiddleware.ValidateTokenViaHub"/> targets. <c>MainNode</c> is the owning
    /// user, so the validating read is permitted for the OWNER and for System, and denied to the
    /// caller in the test — which is what makes the success assertion above evidence that the
    /// impersonation really ran.
    /// </summary>
    private async Task<string> CreateApiToken()
    {
        var rawToken = ValidateTokenRequest.TokenPrefix
                       + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = ValidateTokenRequest.HashToken(rawToken);

        await NodeFactory.CreateNode(new MeshNode(hash[..12], "ApiToken")
        {
            Name = "Auth-bootstrap latch token",
            NodeType = ApiTokenNodeType.NodeType,
            MainNode = TokenOwner,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = TokenOwner,
                UserName = "Token Owner",
                UserEmail = $"{TokenOwner}@example.com",
                Label = "latch",
                CreatedAt = DateTimeOffset.UtcNow,
                Roles = ["Editor"],
            }
        }).Should().Within(StepTimeout).Emit();

        return rawToken;
    }
}

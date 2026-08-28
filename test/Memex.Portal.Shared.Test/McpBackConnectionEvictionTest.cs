using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A revoked MCP back-connection token must stop being handed out (#2302, finding 6).
///
/// <para>🚨 The cache had no eviction path at all: once a token was cached it was returned for the
/// life of the process, so revoking it simply did not take effect — mesh access stayed broken until
/// the portal restarted. The class comment claimed "a revoked token surfaces as a 401 on the next
/// MCP call, which the auth-on-exception path turns into a re-mint"; no such path existed, and
/// neither CLI client reports an <c>/mcp</c> 401 back here, so an <c>Invalidate(userId)</c>
/// contract would have been a method with no caller. Validating before reuse is what makes the
/// revocation land.</para>
/// </summary>
public class McpBackConnectionEvictionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // ApiTokenService is not a registered component in a bare test mesh — construct it the way
    // ApiTokenServiceStaleReadTest does, with a short validation window so the revoked-token read
    // settles quickly rather than waiting out the 8 s production default.
    private ApiTokenService Tokens() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
        Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>())
    {
        ValidationReadTimeout = TimeSpan.FromSeconds(2),
    };

    private McpBackConnectionService Service(ApiTokenService tokens) => new(
        tokens,
        Options.Create(new McpConfiguration { BaseUrl = "https://portal.example" }),
        NullLogger<McpBackConnectionService>.Instance);

    [Fact]
    public async Task A_revoked_cached_token_is_evicted_and_replaced()
    {
        var tokens = Tokens();
        var svc = Service(tokens);
        var userId = $"mcpuser{Guid.NewGuid():N}"[..16];

        var first = await svc.EnsureForUser(userId).FirstAsync().ToTask();
        first.Should().NotBeNull("the first call mints a token");
        var firstToken = first!.BearerToken;

        // Cached: the same token comes back without minting a second one.
        var cached = await svc.EnsureForUser(userId).FirstAsync().ToTask();
        cached!.BearerToken.Should().Be(firstToken, "a valid cached token is reused — that is the hot path");

        // Revoke it the way an operator would.
        var mine = await tokens.GetTokensForUser(userId).FirstAsync().ToTask();
        mine.Should().NotBeEmpty("the mint created a token node for this user");
        (await tokens.RevokeToken(mine[0].NodePath).FirstAsync().ToTask())
            .Should().BeTrue("revocation must succeed for this test to mean anything");

        var afterRevoke = await svc.EnsureForUser(userId).FirstAsync().ToTask();

        afterRevoke.Should().NotBeNull();
        afterRevoke!.BearerToken.Should().NotBe(
            firstToken,
            "the revoked token must not be handed out again — before this fix the cache had no "
            + "eviction path, so it was returned for the life of the process and revoking a "
            + "back-connection token did nothing at all");
    }
}

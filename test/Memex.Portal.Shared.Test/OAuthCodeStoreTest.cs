using System;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Mesh-backed OAuthCodeStore semantics: single-use consume-first codes, and the exact
/// failure reason for every rejection branch (unknown/consumed, expired, client_id,
/// redirect_uri, PKCE). The reason string is what the /token endpoint logs at Warning —
/// the bare "invalid or expired" line made real-world MCP-login failures unattributable
/// (2026-07-02: a failed exchange on memex-cloud could not be told apart from a
/// duplicate-callback burn or a PKCE mismatch).
///
/// <para>
/// The store persists codes as mesh nodes at <c>Admin/OAuthCode/{hashPrefix}</c> — the
/// replica-safety fix for the 2026-07-23 prod outage where /authorize minted the code in
/// pod A's in-memory dictionary and the MCP client's /token exchange landed on pod B
/// ("never issued by this process"). <see cref="TwoStoreInstances_GenerateOnOne_ExchangeOnOther"/>
/// pins exactly that scenario: two store INSTANCES sharing one mesh (two replicas sharing
/// one PG) must exchange each other's codes, and the single-use consume (first delete
/// wins) must hold across instances.
/// </para>
/// </summary>
public class OAuthCodeStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ClientId = "client-1";
    private const string RedirectUri = "http://localhost:12345/callback";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // Same registration the portal wires in ConfigureMemexMesh — the OAuthCode
        // NodeType + AuthorizationCode content type the store persists.
        => base.ConfigureMesh(builder).AddOAuthCodeType();

    /// <summary>
    /// Each call builds an independent store instance on the SAME mesh — the same
    /// relationship two KEDA replicas have to the shared PG-backed mesh.
    /// </summary>
    private OAuthCodeStore NewStore(TimeSpan? codeLifetime = null) => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<OAuthCodeStore>>())
    {
        CodeLifetime = codeLifetime ?? TimeSpan.FromMinutes(5),
    };

    private static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Issues a code and waits until its node is readable — absorbs create→routing
    /// visibility lag (sanctioned Interval+re-read pattern, WritingTests.md) so the
    /// exchange under test exercises its own branch, not read-side lag.
    /// </summary>
    private async Task<string> Issue(OAuthCodeStore store, string? challenge = null, string? method = null)
    {
        var code = await store.GenerateCode("rbuergi", "Roland", "rbuergi@systemorph.com",
                ClientId, RedirectUri, challenge, method)
            .Should().Within(30.Seconds()).Emit();

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ReadNode(OAuthCodeStore.PathForCode(code)))
            .Should().Within(30.Seconds()).Match(n => n is not null);

        return code;
    }

    private Task<CodeExchangeResult> Exchange(
        OAuthCodeStore store, string code,
        string clientId = ClientId, string redirectUri = RedirectUri, string? verifier = null)
        => store.ExchangeCode(code, clientId, redirectUri, verifier)
            .Should().Within(30.Seconds()).Emit();

    [Fact]
    public async Task Exchange_WithMatchingParameters_ReturnsEntry_NoReason()
    {
        var store = NewStore();
        var code = await Issue(store);

        var result = await Exchange(store, code);

        result.Entry.Should().NotBeNull();
        result.FailureReason.Should().BeNull();
        result.Entry!.UserId.Should().Be("rbuergi");
        result.Entry.UserEmail.Should().Be("rbuergi@systemorph.com");
    }

    [Fact]
    public async Task Exchange_UnknownCode_ReportsUnknown()
    {
        var store = NewStore();

        var result = await Exchange(store, "no-such-code");

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("unknown or already consumed");
    }

    [Fact]
    public async Task Exchange_SecondAttempt_IsConsumed()
    {
        var store = NewStore();
        var code = await Issue(store);
        var first = await Exchange(store, code);
        first.Entry.Should().NotBeNull();

        var second = await Exchange(store, code);

        second.Entry.Should().BeNull();
        second.FailureReason.Should().Contain("unknown or already consumed");
    }

    [Fact]
    public async Task Exchange_ExpiredCode_ReportsExpired()
    {
        // Zero lifetime → the code is already expired by the time the exchange
        // validates it (deterministic — no sleeping). The consume-first delete has
        // already removed the node, so an expired code is rejected AND gone.
        var store = NewStore(codeLifetime: TimeSpan.Zero);
        var code = await Issue(store);

        var result = await Exchange(store, code);

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("expired");

        // reject + delete: the expired code's node was consumed by the rejection.
        var after = await ReadNode(OAuthCodeStore.PathForCode(code)).Should().Within(30.Seconds()).Emit();
        after.Should().BeNull();
    }

    [Fact]
    public async Task Exchange_ClientIdMismatch_ReportsClientId()
    {
        var store = NewStore();
        var code = await Issue(store);

        var result = await Exchange(store, code, clientId: "other-client");

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("client_id mismatch");
    }

    [Fact]
    public async Task Exchange_RedirectUriMismatch_ReportsRedirectUri()
    {
        var store = NewStore();
        var code = await Issue(store);

        var result = await Exchange(store, code, redirectUri: "http://localhost:9/other");

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("redirect_uri mismatch");
    }

    [Fact]
    public async Task Exchange_S256Pkce_RoundTrips()
    {
        var store = NewStore();
        const string verifier = "the-verifier-string-with-enough-entropy";
        var code = await Issue(store, Challenge(verifier), "S256");

        var result = await Exchange(store, code, verifier: verifier);

        result.Entry.Should().NotBeNull();
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task Exchange_PlainPkce_RoundTrips()
    {
        // plain method: challenge == verifier, compared verbatim.
        var store = NewStore();
        const string verifier = "plain-verifier-with-enough-entropy-0123456789";
        var code = await Issue(store, verifier, "plain");

        var result = await Exchange(store, code, verifier: verifier);

        result.Entry.Should().NotBeNull();
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task Exchange_MissingVerifier_ReportsMissing()
    {
        var store = NewStore();
        var code = await Issue(store, Challenge("v"), "S256");

        var result = await Exchange(store, code);

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("code_verifier missing");
    }

    [Fact]
    public async Task Exchange_WrongVerifier_ReportsPkceFailure()
    {
        var store = NewStore();
        var code = await Issue(store, Challenge("right-verifier"), "S256");

        var result = await Exchange(store, code, verifier: "wrong-verifier");

        result.Entry.Should().BeNull();
        result.FailureReason.Should().Contain("PKCE verification failed");
    }

    [Fact]
    public async Task TwoStoreInstances_GenerateOnOne_ExchangeOnOther()
    {
        // The prod 2026-07-23 outage shape: /authorize on replica A, /token on replica B.
        // Two independent store instances share the mesh the way two pods share PG —
        // B must be able to exchange A's code, and the single-use consume must then
        // hold on EVERY replica (a replay against A is a lost first-delete-wins race,
        // never a second success).
        var replicaA = NewStore();
        var replicaB = NewStore();

        var code = await Issue(replicaA);

        var onB = await Exchange(replicaB, code);
        onB.Entry.Should().NotBeNull();
        onB.FailureReason.Should().BeNull();
        onB.Entry!.UserId.Should().Be("rbuergi");

        var replayOnA = await Exchange(replicaA, code);
        replayOnA.Entry.Should().BeNull();
        replayOnA.FailureReason.Should().Contain("unknown or already consumed");
    }
}

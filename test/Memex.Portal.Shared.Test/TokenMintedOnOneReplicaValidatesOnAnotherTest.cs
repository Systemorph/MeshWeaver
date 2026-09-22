using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A bearer token minted by one replica is accepted by a replica that never saw it minted.
///
/// <para><b>Why this exists as a test and not as an argument.</b> #5074 reported an MCP session that
/// alternated between working and answering 401 "token expired", and reasoned from outside the
/// cluster that the token must live in the issuing process's memory. It no longer does —
/// <c>ApiTokenService</c> reads the index row and the token row STRAIGHT off the shared storage
/// adapter, and <c>CreateToken</c> confirms both are committed before the raw token leaves the
/// server. But "it no longer does" is a claim about code, and the two shapes it replaced
/// (an in-memory dictionary, then a read that rode the mesh's cross-silo per-node-hub path) were
/// both invisible to every single-instance test in the suite. A claim that only a second replica
/// could falsify needs a second replica, or it is not being tested at all.</para>
///
/// <para>Two <c>ApiTokenService</c> instances over one storage adapter is the same relationship two
/// pods have to one Postgres: independent service state, one authoritative store. The companion
/// case for authorization codes is <c>OAuthCodeStoreTest</c>'s two-store exchange.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> validation ever reads instance-local state again: the mint happens on
/// one instance and every assertion is made on the other.
/// <see cref="AnUnknownTokenIsADefinitiveNegativeOnEitherReplica"/> is the control in the other
/// direction — a validator hardwired to Valid would pass the first test and fail this one, and it
/// also pins the distinction the 401/503 split rests on: an unknown token is
/// <see cref="TokenValidationStatus.Invalid"/>, never <see cref="TokenValidationStatus.Unavailable"/>.</para>
/// </summary>
public class TokenMintedOnOneReplicaValidatesOnAnotherTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string UserId = "replica-split-user";
    private const string UserEmail = "replica-split@example.com";

    /// <summary>
    /// One "replica": its own service instance, the SHARED store. Nothing is passed between the two
    /// instances except the raw token, exactly as a client passes it.
    /// </summary>
    private ApiTokenService Replica() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    [Fact]
    public async Task AFreshTokenIsValidOnTheReplicaThatDidNotMintIt()
    {
        var minting = Replica();
        var validating = Replica();

        var created = await minting
            .CreateToken(UserId, "Replica Split", UserEmail, "MintedOnA")
            .Should().Emit();

        var verdict = await validating.Validate(created.RawToken).Should().Emit();

        verdict.Status.Should().Be(
            TokenValidationStatus.Valid,
            "the token's rows are in the shared store before the raw token is handed out, so the "
            + "replica that receives the next request resolves it without having minted it — "
            + "reason given: {0}",
            verdict.Reason ?? "(none)");
        verdict.Token!.UserId.Should().Be(UserId);
    }

    [Fact]
    public async Task AnUnknownTokenIsADefinitiveNegativeOnEitherReplica()
    {
        var verdict = await Replica()
            .Validate("mw_this-token-was-never-minted-anywhere")
            .Should().Emit();

        verdict.Status.Should().Be(
            TokenValidationStatus.Invalid,
            "an absent row is a verdict, not a failure to reach one — Unavailable is reserved for a "
            + "store that could not be read, and answering 401 for that is what makes a client "
            + "discard a credential that was never wrong");
    }
}

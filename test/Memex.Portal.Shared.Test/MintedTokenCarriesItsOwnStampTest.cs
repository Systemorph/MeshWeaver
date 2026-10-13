using System;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #6636: the OAuth exchange ranks this client's live tokens for eviction by the minted token's
/// creation stamp. That stamp is the one the mint WROTE, carried on
/// <see cref="TokenCreationResult.CreatedAt"/> — it used to be re-read off the node's content with a
/// cast whose fallback was "now", taken after the read-back and therefore LATER than the real stamp.
/// </summary>
public class MintedTokenCarriesItsOwnStampTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string User = "mintstampuser";

    private Task<T> Within<T>(IObservable<T> source, string because = "") =>
        source.Should().Within(TestTimeouts.Convergence).Emit(because, TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheResult_CarriesTheStampTheMintWrote_NotOneTakenAfterTheReadBack()
    {
        var service = new ApiTokenService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            new CapturingLogger<ApiTokenService>());

        var before = DateTimeOffset.UtcNow;
        var creation = await Within(service.CreateOAuthToken(User, User, $"{User}@example.com", "stamp-client", null));
        var after = DateTimeOffset.UtcNow;

        var stored = creation.Node.ContentAs<ApiToken>(Mesh.JsonSerializerOptions);
        stored.Should().NotBeNull("the minted node carries its token record");
        creation.CreatedAt.Should().Be(stored!.CreatedAt,
            "the eviction key is the stamp the mint wrote, not a value taken afterwards");
        creation.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}

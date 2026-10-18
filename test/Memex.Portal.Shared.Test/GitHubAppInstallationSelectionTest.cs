using System;
using System.Net.Http;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="GitHubAppTokenService.GetInstallation"/> names the installation the token was MINTED
/// for — the one the mint selected — and never the configured owner when the mint did not use it.
/// A GitSync activity names its read identity from this (<see cref="GitHubSyncService.ReadIdentityFor"/>),
/// so an operator reading "installation N (account)" reads the account that performed the read.
/// </summary>
public class GitHubAppInstallationSelectionTest : IAsyncDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    // One registry per test (xUnit builds one instance per test), owned here and torn down below:
    // a minted token runs on its HTTP pool, whose cancel thread lives until the registry is disposed.
    private readonly IoPoolRegistry pools = new();

    private const string TwoInstallations =
        """[{"id": 11, "account": {"login": "Contoso"}}, {"id": 33, "account": {"login": "Fabrikam"}}]""";

    private GitHubAppTokenService Service(RSA key, long? installationId, string? owner) =>
        new(pools,
            Options.Create(new GitHubAppOptions
            {
                ClientId = "Iv23liTestApp",
                PrivateKey = key.ExportRSAPrivateKeyPem(),
                InstallationId = installationId,
                InstallationOwner = owner,
            }),
            httpClient: new HttpClient(new GitSyncNamesItsReadIdentityTest.InstallationsHandler(TwoInstallations)));

    [Fact]
    public async Task AConfiguredOwnerThatMatches_IsTheSelectedInstallation()
    {
        using var key = RSA.Create(2048);
        var installation = await Service(key, null, "Fabrikam").GetInstallation().FirstAsync().Timeout(Budget).Await();
        Assert.Equal(new GitHubAppInstallation(33, "Fabrikam"), installation);
    }

    /// <summary>The regression: no installation on the configured owner — the mint falls back to the
    /// first, and the reading must name THAT one, not the owner it was configured with.</summary>
    [Fact]
    public async Task AConfiguredOwnerThatMatchesNothing_NamesTheFallbackTheMintUsed()
    {
        using var key = RSA.Create(2048);
        var installation = await Service(key, null, "Northwind").GetInstallation().FirstAsync().Timeout(Budget).Await();
        Assert.Equal(new GitHubAppInstallation(11, "Contoso"), installation);
    }

    /// <summary>A pinned id is named by its id alone: nothing was asked of GitHub about its account,
    /// so the configured owner is not presented as if it had been verified.</summary>
    [Fact]
    public async Task APinnedInstallationId_IsNamedByItsIdAlone()
    {
        using var key = RSA.Create(2048);
        var installation = await Service(key, 162001736, "Northwind").GetInstallation().FirstAsync().Timeout(Budget).Await();
        Assert.Equal(new GitHubAppInstallation(162001736, null), installation);
    }

    /// <summary>Disposes the registry and waits for its pools to report they have unwound —
    /// <see cref="IoPoolRegistry.Dispose"/> only starts the cleanup.</summary>
    public async ValueTask DisposeAsync()
    {
        pools.Dispose();
        await pools.Disposed.FirstAsync().Timeout(Budget).Await();
        GC.SuppressFinalize(this);
    }
}

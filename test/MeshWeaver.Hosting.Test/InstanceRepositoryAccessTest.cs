using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The instance's OWN repository access on a real mesh (Doc/Architecture/InstanceRepositoryAccess):
/// <list type="bullet">
/// <item>a package source a global administrator adds is a durable node in the Admin partition, read
/// LIVE by the catalog, and nobody else can add or remove one;</item>
/// <item>the GitHub App manifest flow stores the App's credentials ENCRYPTED and only through a
/// callback whose state matches the flow the same administrator started — once;</item>
/// <item>once connected, the token service and the per-source credential resolution use the App
/// with no restart; a disconnect stops it.</item>
/// </list>
/// </summary>
public class InstanceRepositoryAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";
    private const string PlatformAdmin = "platform-boss";
    private const string OtherAdmin = "platform-colleague";
    private const string PlainUser = "plain-jane";
    private const string Repo = "https://github.com/acme/reporting";
    private const string BaseUrl = "https://portal.example.com";

    private static readonly string MasterKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode(PlainUser) { Name = "Plain Jane", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(OtherAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(PlainUser, "Admin", PlainUser))
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services.AddSingleton<IConfiguration>(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Ai:KeyProtection:MasterKey"] = MasterKey,
                    }).Build());
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private InstancePackageSourceCatalog Sources => Mesh.ServiceProvider.GetRequiredService<InstancePackageSourceCatalog>();
    private InstanceSecretCatalog Secrets => Mesh.ServiceProvider.GetRequiredService<InstanceSecretCatalog>();
    private GitHubAppTokenService Tokens => Mesh.ServiceProvider.GetRequiredService<GitHubAppTokenService>();

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    private Task<T> As<T>(string user, Func<IObservable<T>> work)
    {
        using (Access.SwitchAccessContext(Identity(user)))
            return work().FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private Task<MeshNode?> Stored(string path) =>
        Access.RunAsSystem(() => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}")).Take(1)
                .Select(c => c.Items.FirstOrDefault()))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private static string RefusalKey(Exception ex) =>
        GitSyncRefusalException.TryGet(ex, out var refusal) ? refusal.Key : ex.Message;

    // ───────────────────────────── package sources ─────────────────────────────

    [Fact]
    public async Task AGlobalAdmin_AddsADurableSource_ThatTheCatalogReadsLive_AndOnlyAnAdminCanChangeIt()
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => As(PlainUser, () => InstancePackageSources.Add(Mesh, Repo)));
        Assert.Equal("instanceSources.error.notAdmin", RefusalKey(refused));
        Assert.Null(await Stored(InstancePackageSources.PathOf(Repo)!));

        var added = await As(PlatformAdmin, () => InstancePackageSources.Add(Mesh, Repo + ".git", "release"));
        Assert.Equal(Repo, added.Repo);
        Assert.Equal("release", added.Ref);
        Assert.Equal(PlatformAdmin, added.AddedBy);

        var node = await Stored(InstancePackageSources.PathOf(Repo)!);
        Assert.NotNull(node);
        Assert.Equal(InstancePackageSources.NodeType, node!.NodeType);

        await Sources.Changes.Where(_ => Sources.For(Repo) is not null)
            .Should().Within(Budget).Emit("an added source is what the catalog reads, without a restart");

        var chosen = await As(PlatformAdmin, () => InstancePackageSources.SetInstallByDefault(Mesh, Repo, ["Reporting"]));
        Assert.Equal(["Reporting"], chosen.InstallByDefault);
        await Sources.Changes.Where(_ => Sources.For(Repo)?.InstallByDefault.Count == 1)
            .Should().Within(Budget).Emit("the install-by-default choice is read live");
        Assert.Equal(["reporting/Reporting"], InstancePackageSources.InstallEntries(Sources.Sources));

        var removeRefused = await Assert.ThrowsAnyAsync<Exception>(
            () => As(PlainUser, () => InstancePackageSources.Remove(Mesh, Repo)));
        Assert.Equal("instanceSources.error.notAdmin", RefusalKey(removeRefused));
        Assert.NotNull(await Stored(InstancePackageSources.PathOf(Repo)!));

        await As(PlatformAdmin, () => InstancePackageSources.Remove(Mesh, Repo));
        await Sources.Changes.Where(_ => Sources.For(Repo) is null)
            .Should().Within(Budget).Emit("a removed source leaves the catalog");
    }

    [Fact]
    public async Task ASourceThatIsNotAGitHubRepository_IsRefused_AndNothingIsStored()
    {
        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => As(PlatformAdmin, () => InstancePackageSources.Add(Mesh, "https://gitlab.com/acme/reporting")));
        Assert.Equal("instanceSources.error.notGitHubRepo", RefusalKey(refused));
    }

    // ───────────────────────────── the GitHub App ─────────────────────────────

    private static GitHubAppManifestConversion Conversion(string pem) =>
        new(4242, "meshweaver-portal", "MeshWeaver portal", "Iv23liInstance", pem, "hook-secret", "acme",
            "https://github.com/apps/meshweaver-portal");

    [Fact]
    public async Task TheManifestFlow_StoresTheAppEncrypted_AndTheIdentityIsUsedLive_UntilADisconnect()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var conversions = 0;

        Assert.False(Tokens.HasInstanceApp);

        var notAdmin = await Assert.ThrowsAnyAsync<Exception>(
            () => As(PlainUser, () => InstanceGitHubApp.Start(Mesh, BaseUrl, "acme")));
        Assert.Equal("instanceApp.error.notAdmin", RefusalKey(notAdmin));

        var flow = await As(PlatformAdmin, () => InstanceGitHubApp.Start(Mesh, BaseUrl, "acme"));
        Assert.StartsWith("https://github.com/organizations/acme/settings/apps/new?state=", flow.PostUrl);
        var pending = (await Stored(InstanceGitHubApp.Path))!.ContentAs<InstanceGitHubAppContent>(Mesh.JsonSerializerOptions)!;
        Assert.Equal(InstanceGitHubApp.HashState(flow.State), pending.PendingStateHash);
        Assert.Equal(PlatformAdmin, pending.PendingBy);
        Assert.DoesNotContain(flow.State, System.Text.Json.JsonSerializer.Serialize(pending));

        // Another administrator cannot complete it — and the attempt ends the pending flow.
        var other = await Assert.ThrowsAnyAsync<Exception>(() => As(OtherAdmin,
            () => InstanceGitHubApp.Complete(Mesh, "code", flow.State, flow.State,
                _ => { conversions++; return Observable.Return(Conversion(pem)); })));
        Assert.Equal("instanceApp.error.otherAdmin", RefusalKey(other));
        Assert.Equal(0, conversions);

        // …so the original admin's now-replayed callback finds no pending flow either.
        var replay = await Assert.ThrowsAnyAsync<Exception>(() => As(PlatformAdmin,
            () => InstanceGitHubApp.Complete(Mesh, "code", flow.State, flow.State,
                _ => { conversions++; return Observable.Return(Conversion(pem)); })));
        Assert.Equal("instanceApp.error.noPendingFlow", RefusalKey(replay));
        Assert.Equal(0, conversions);
        Assert.Null(await Stored(InstanceSecrets.PathOf(InstanceGitHubApp.PrivateKeyKey)));

        // A fresh flow, with a cookie from ANOTHER flow: refused before GitHub is asked.
        var second = await As(PlatformAdmin, () => InstanceGitHubApp.Start(Mesh, BaseUrl, "acme"));
        var forged = await Assert.ThrowsAnyAsync<Exception>(() => As(PlatformAdmin,
            () => InstanceGitHubApp.Complete(Mesh, "code", second.State, flow.State,
                _ => { conversions++; return Observable.Return(Conversion(pem)); })));
        Assert.Equal("instanceApp.error.stateMismatch", RefusalKey(forged));
        Assert.Equal(0, conversions);

        // The real one.
        var third = await As(PlatformAdmin, () => InstanceGitHubApp.Start(Mesh, BaseUrl, "acme"));
        var app = await As(PlatformAdmin, () => InstanceGitHubApp.Complete(Mesh, "code", third.State, third.State,
            code => { conversions++; Assert.Equal("code", code); return Observable.Return(Conversion(pem)); }));
        Assert.Equal(1, conversions);
        Assert.Equal("meshweaver-portal", app.Slug);
        Assert.Equal("acme", app.Owner);
        Assert.Equal(PlatformAdmin, app.ConnectedBy);
        Assert.Null(app.PendingStateHash);

        // Encrypted at rest, every one of the four.
        foreach (var key in InstanceGitHubApp.SecretKeys)
        {
            var secret = (await Stored(InstanceSecrets.PathOf(key)))!.ContentAs<InstanceSecretContent>(Mesh.JsonSerializerOptions)!;
            Assert.StartsWith("enc:", secret.EncryptedValue);
        }
        var keyNode = await Stored(InstanceSecrets.PathOf(InstanceGitHubApp.PrivateKeyKey));
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(keyNode, Mesh.JsonSerializerOptions));
        var metadata = await Stored(InstanceGitHubApp.Path);
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(metadata, Mesh.JsonSerializerOptions));
        Assert.DoesNotContain("hook-secret", System.Text.Json.JsonSerializer.Serialize(metadata, Mesh.JsonSerializerOptions));

        // Live: the token service and the webhook verifier see the App without a restart.
        await Secrets.Changes.Where(_ => Tokens.HasInstanceApp)
            .Should().Within(Budget).Emit("a connected App is the instance identity at once");
        Assert.True(Tokens.IsConfigured);
        Assert.False(Tokens.IsDeploymentConfigured);
        Assert.Equal("hook-secret", InstanceGitHubApp.WebhookSecret(Secrets));
        Assert.Equal("Iv23liInstance", InstanceGitHubApp.Options(Secrets)!.ClientId);

        // An instance source is now read with it.
        await As(PlatformAdmin, () => InstancePackageSources.Add(Mesh, Repo));
        await Sources.Changes.Where(_ => Sources.For(Repo) is not null).Should().Within(Budget).Emit("the source is read");
        var credential = Mesh.ServiceProvider.GetRequiredService<GitSourceCredentials>().For(Repo);
        Assert.NotNull(credential);
        Assert.True(credential!.InstanceApp);

        // Disconnect: the identity is gone, nothing falls back to it.
        await As(PlatformAdmin, () => InstanceGitHubApp.Disconnect(Mesh));
        await Secrets.Changes.Where(_ => !Tokens.HasInstanceApp)
            .Should().Within(Budget).Emit("a disconnected App is no identity any more");
        Assert.Null(Mesh.ServiceProvider.GetRequiredService<GitSourceCredentials>().For(Repo));
        Assert.Null(InstanceGitHubApp.WebhookSecret(Secrets));
    }
}

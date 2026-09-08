#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The bundle index (<c>/api/plugins/bundles/index.json</c>) naming each bundle's OCI
/// <c>artifact</c> (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>) — from the registry's
/// record of pushed publications, the <see cref="IPublicationArtifacts"/> seam. Additive: the
/// field is present on every bundle, <c>null</c> where nothing was pushed, and with the platform's
/// default (which records nothing) it is <c>null</c> everywhere, so a pre-artifact consumer and a
/// pre-artifact registry keep exactly today's shape.
///
/// <para>The same host shape as <see cref="PluginBundleAnchorTest"/>: the REAL mesh, the real
/// registration and install path, the routes on a TestServer; the artifacts record is a fake
/// registered on the request's services — the two-step resolution a host uses.</para>
/// </summary>
public class PluginBundleIndexArtifactTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PushedPackage = "IndexArtifactPushed";
    private const string PlainPackage = "IndexArtifactPlain";
    private const string Source = "Plugins";
    private const string Version = "2.1.0";
    private const string Instance = "index-artifact-consumer";

    private static readonly string Reference =
        $"cr.example.test/plugins/{Source}/{PushedPackage}@sha256:" + new string('a', 64);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private Task<string> RegisterInstance(params string[] defaultGrants) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                        new KeyValuePair<string, string?>(
                            $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                    .Build())
            .Register("index-owner", "Index Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();

    private Task<InstallResult> InstallPackage(string id) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = id,
                    Name = id,
                    Kind = PackageKind.Content,
                    TargetPartition = id,
                    SourceFolder = id,
                    Version = "1.0.0",
                    ReleasedVersion = Version,
                    Source = Source,
                },
                [new PackageFile($"{id}/Doc.md", $"# {id}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .Await();

    private async Task<WebApplication> StartBundleHost(IPublicationArtifacts? artifacts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        if (artifacts is not null)
            builder.Services.AddSingleton(artifacts);

        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    private static async Task<Dictionary<string, string?>> ArtifactsInIndex(WebApplication app, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, PluginBundleEndpoints.RoutePrefix + "/index.json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var response = await app.GetTestClient().SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("bundles").EnumerateArray().ToDictionary(
            b => b.GetProperty("plugin").GetString()!,
            b => b.TryGetProperty("artifact", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : b.TryGetProperty("artifact", out _) ? null : "(absent)");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheIndexNamesTheArtifact_ForABundleThatWasPushed_AndNullForOneThatWasNot()
    {
        await InstallPackage(PushedPackage);
        await InstallPackage(PlainPackage);
        var key = await RegisterInstance($"{Source}/*");

        var app = await StartBundleHost(new RecordedArtifacts(
            new PublicationArtifact(Source, PushedPackage, Version, Reference)));
        await using var _ = app;

        var artifacts = await ArtifactsInIndex(app, key);

        artifacts.Keys.Should().Contain([PushedPackage, PlainPackage]);
        artifacts[PushedPackage].Should().Be(Reference,
            "the registry pushed this bundle and its index says where, by digest");
        artifacts[PlainPackage].Should().BeNull(
            "the field is present and null on a bundle nothing pushed — the consumer takes the HTTP route");
    }

    [Fact(Timeout = 300_000)]
    public async Task WithThePlatformDefault_EveryArtifactIsNull()
    {
        await InstallPackage(PlainPackage);
        var key = await RegisterInstance($"{Source}/*");

        var app = await StartBundleHost(artifacts: null);
        await using var _ = app;

        var artifacts = await ArtifactsInIndex(app, key);

        artifacts.Keys.Should().Contain(PlainPackage);
        artifacts[PlainPackage].Should().BeNull(
            "AddPluginCatalog registers NoPublicationArtifacts, which records nothing");
    }

    [Fact]
    public void TheLookup_MatchesPackageAndVersion_AndSourceOnlyWhenBothStateOne()
    {
        IReadOnlyList<PublicationArtifact> pushed =
        [
            new(Source, "Pkg", "1.0.0", "cr/plugins/Plugins/Pkg@sha256:" + new string('1', 64)),
            new(null, "Loose", "2.0.0", "cr/plugins/x/Loose@sha256:" + new string('2', 64)),
        ];

        pushed.ReferenceFor(Source, "pkg", "1.0.0").Should().Be(pushed[0].Reference, "package ids are case-insensitive");
        pushed.ReferenceFor("Education", "Pkg", "1.0.0").Should().BeNull("a different source is a different artifact");
        pushed.ReferenceFor(null, "Pkg", "1.0.0").Should().Be(pushed[0].Reference, "an entry without a source matches on package and version");
        pushed.ReferenceFor(Source, "Pkg", "1.0.1").Should().BeNull("the version is compared exactly");
        pushed.ReferenceFor(Source, "Pkg", null, "1.0.0").Should().Be(pushed[0].Reference, "versions are tried in order, nulls skipped");
        pushed.ReferenceFor("Anything", "Loose", "2.0.0").Should().Be(pushed[1].Reference, "a record without a source matches wherever the package is served from");
    }

    private sealed class RecordedArtifacts(params PublicationArtifact[] artifacts) : IPublicationArtifacts
    {
        public IObservable<IReadOnlyList<PublicationArtifact>> Read() =>
            Observable.Return((IReadOnlyList<PublicationArtifact>)artifacts);
    }
}

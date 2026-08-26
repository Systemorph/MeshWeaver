using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A module PUBLISHED to the registry must reach the shelf with its STATIC WEB ASSETS, not just
/// its assemblies.
///
/// <para>🚨 Why this needs a test of its own: nothing else fails when the assets are dropped. The
/// publish returns 200, the shelf entry is recorded, the module lands, it loads, and every page
/// still renders — only unstyled, and only for the packs whose image copy has been retired. That
/// is exactly how it shipped: <c>Validate</c> already took the assets and <c>Accepted</c> already
/// carried them to <c>ShelveModule</c>, but the endpoint passed neither argument, so every module
/// ever published landed with assemblies alone. Measured in production 2026-08-25 (#2221):
/// DefaultViews' bundle declares 254 static assets while its landed generation held 11 files and
/// no <c>wwwroot</c> at all; <c>MeshWeaver.Blazor.EntityViews.styles.css</c> had been 404 since
/// #2188 retired that pack's image copy, and the portal was styled only because Views' and Graph's
/// image copies were still there.</para>
///
/// <para>So the assertion is deliberately about BYTES ON DISK under the landed module directory,
/// not about a status code or a log line: the consumer's stylesheet request is a file lookup, and
/// only a file satisfies it.</para>
/// </summary>
public class PluginBundlePublishAssetsTest : IDisposable
{
    private const string Token = "publish-token-for-this-test";
    private const string Module = "MeshWeaver.Test.ViewPack";
    private const string Plugin = "TestViewPack";
    private const string AssetPath = "wwwroot/MeshWeaver.Test.ViewPack.styles.css";
    private static readonly byte[] AssetBytes = Encoding.UTF8.GetBytes(".mw-test{color:#bada55}");

    // 🚨 A SECOND asset, and a NESTED one deliberately: one file landing is not evidence that the
    // set landed, so a partial drop has to fail here too — and a pack's collocated JS is requested
    // at its relative path (_content/<pack>/Components/…), so flattening it would 404 exactly like
    // dropping it. Both are asserted byte-exact below.
    private const string NestedAssetPath = "wwwroot/Components/TestView.razor.js";
    private static readonly byte[] NestedAssetBytes =
        Encoding.UTF8.GetBytes("export function init(){ return 'mw-test'; }");

    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-publish-assets-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The publish endpoint lands the bundle's assets beside its assemblies — the file a browser
    /// would request exists, with the bytes the producer packed.
    /// </summary>
    [Fact]
    public async Task PublishedModule_LandsItsStaticAssets()
    {
        var response = await Publish(BuildBundle(withAssets: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var (relative, expected) in new[]
                 {
                     (AssetPath, AssetBytes),
                     (NestedAssetPath, NestedAssetBytes),
                 })
        {
            var landed = LandedFile(relative);
            Assert.True(landed is not null,
                $"the published bundle declared '{relative}' but no landed module directory under "
                + $"'{root}' carries it — the shelf dropped part or all of the pack's static web "
                + "assets, so consumers of this module render unstyled or 404 its collocated JS "
                + "(#2221)");
            Assert.Equal(expected, File.ReadAllBytes(landed!));
        }
    }

    /// <summary>
    /// The entry assembly still lands — the assertion above must be evidence about ASSETS, not
    /// about a publish path that happens to write everything or nothing.
    /// </summary>
    [Fact]
    public async Task PublishedModule_LandsItsEntryAssembly()
    {
        var response = await Publish(BuildBundle(withAssets: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LandedFile(Module + ".dll") is not null,
            "the entry assembly did not land — this test's own premise is broken, so its asset "
            + "assertion would prove nothing");
    }

    /// <summary>
    /// A bundle that declares NO assets lands its assembly and writes no <c>wwwroot</c> — the
    /// asset path is driven by what the producer packed, never fabricated by the shelf.
    /// </summary>
    [Fact]
    public async Task PublishedModuleWithoutAssets_LandsNoWwwroot()
    {
        var response = await Publish(BuildBundle(withAssets: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LandedFile(Module + ".dll") is not null, "the entry assembly did not land");
        Assert.Null(LandedFile(AssetPath));
        Assert.Null(LandedFile(NestedAssetPath));
    }

    /// <summary>The landed path of one module-relative file, or null when no generation carries it.</summary>
    private string? LandedFile(string relativePath)
    {
        var modules = Path.Combine(root, "modules");
        if (!Directory.Exists(modules))
            return null;
        foreach (var directory in Directory.EnumerateDirectories(modules))
        {
            var candidate = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private async Task<HttpResponseMessage> Publish(byte[] bundle)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration[ModulePublish.TokenConfigKey] = Token;
        // The landing seam: a real service writing under this test's own root, so the assertion
        // reads the bytes the production path would have written.
        builder.Services.AddSingleton(_ => new ModuleLandingService(null, root));

        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();

        var client = app.GetTestClient();
        var content = new ByteArrayContent(bundle);
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        await app.StopAsync();
        return response;
    }

    /// <summary>
    /// A minimal but REAL module bundle: the same writer the packer uses, so the manifest shape and
    /// the entry paths are the production ones rather than this test's idea of them.
    /// </summary>
    private static byte[] BuildBundle(bool withAssets)
    {
        var assembly = Encoding.UTF8.GetBytes("not-a-real-assembly-but-real-bytes");
        var entries = new List<NuGetPackageWriter.Entry>
        {
            new(NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                () => new MemoryStream(assembly)),
        };
        if (withAssets)
        {
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor(AssetPath),
                () => new MemoryStream(AssetBytes)));
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor(NestedAssetPath),
                () => new MemoryStream(NestedAssetBytes)));
        }

        var manifest = new BundleReader.Manifest(
            Plugin,
            "1.0.0",
            FrameworkMvid: "00000000000000000000000000000000",
            Assemblies: null,
            Module: new BundleReader.ModuleRef(
                Module,
                [Module + ".dll"],
                MinMeshVersion: null,
                StaticAssets: withAssets ? [AssetPath, NestedAssetPath] : null));

        using var stream = new MemoryStream();
        NuGetPackageWriter.Write(
            stream,
            new MeshWeaver.Plugin.Packaging.PluginManifest(
                Plugin,
                MeshWeaver.Plugin.Packaging.PluginManifest.IdPrefix + Plugin,
                "1.0.0",
                "a view pack for the publish-assets guard",
                MinMeshVersion: null,
                Requires: ImmutableArray<string>.Empty),
            frameworkVersion: "3.0.0",
            entries,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

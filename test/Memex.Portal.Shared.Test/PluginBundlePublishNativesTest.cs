using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
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
/// A module PUBLISHED to the registry reaches the shelf with its RID-specific NATIVE payloads laid
/// out where the module loader probes them, and the shelf RE-SERVES them (#4126, stages 2 and 3).
///
/// <para>🚨 <b>Why this needs a test of its own, exactly like
/// <see cref="PluginBundlePublishAssetsTest"/> before it: nothing else fails when the natives are
/// dropped.</b> Stage 1 (#4239) made the bundle CARRY them under
/// <c>meshweaver/modulenatives/</c>, declared in the manifest — and the publish then returned 200,
/// the entry was recorded, the module landed and loaded, and the first P/Invoke threw
/// <c>DllNotFoundException</c>, because the landing wrote the assemblies and the <c>wwwroot</c>
/// tree and nothing else. Carried, declared, ignored.</para>
///
/// <para>The assertion is about BYTES ON DISK at the EXACT path <c>ModuleNativeAssets</c> probes —
/// <c>modules/&lt;generation&gt;/runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> — because the
/// resolver composes that path from four segments and has no recursive walk: a native landed
/// anywhere else reads as shipped and behaves as absent.</para>
/// </summary>
public class PluginBundlePublishNativesTest : IDisposable
{
    private const string Token = "publish-token-for-this-test";
    private const string Module = "MeshWeaver.Test.Sqlite";
    private const string Plugin = "TestSqlite";

    // 🚨 TWO RIDs deliberately: one file landing is not evidence that the SET landed, and a
    // partial drop would otherwise pass. They are also the two shapes a real native package emits
    // (a .so and a .dylib), so a filter that happened to key on an extension fails here.
    private const string NativePath = "runtimes/linux-x64/native/libe_sqlite3.so";
    private const string SecondNativePath = "runtimes/osx-arm64/native/libe_sqlite3.dylib";
    private static readonly byte[] NativeBytes = Encoding.UTF8.GetBytes("ELF-linux-x64-engine");
    private static readonly byte[] SecondNativeBytes = Encoding.UTF8.GetBytes("MACHO-osx-arm64-engine");

    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-publish-natives-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// THE defect: the publish endpoint lands the bundle's natives under the module folder, at the
    /// layout the loader probes, byte-exact.
    /// </summary>
    [Fact]
    public async Task PublishedModule_LandsItsNativePayloads_WhereTheLoaderProbes()
    {
        var response = await Publish(BuildBundle(withNatives: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var (relative, expected) in new[]
                 {
                     (NativePath, NativeBytes),
                     (SecondNativePath, SecondNativeBytes),
                 })
        {
            var landed = LandedFile(relative);
            Assert.True(landed is not null,
                $"the published bundle declared native '{relative}' and no landed module directory "
                + $"under '{root}' carries it — the shelf dropped the module's engine, so every "
                + "consumer downstream gets a module that lands, loads, and throws "
                + "DllNotFoundException at its first P/Invoke (#4126)");
            Assert.Equal(expected, File.ReadAllBytes(landed!));
        }
    }

    /// <summary>
    /// The entry assembly still lands — the assertion above must be evidence about NATIVES, not
    /// about a publish path that happens to write everything or nothing.
    /// </summary>
    [Fact]
    public async Task PublishedModule_LandsItsEntryAssembly()
    {
        var response = await Publish(BuildBundle(withNatives: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LandedFile(Module + ".dll") is not null,
            "the entry assembly did not land — this test's own premise is broken, so its native "
            + "assertion would prove nothing");
    }

    /// <summary>
    /// Stage 3: what the shelf HOLDS is what the registry RE-SERVES. A consumer never reads the
    /// upload's archive — it fetches what the serve side collects — so a native on the shelf that
    /// the collect drops is unreachable for every instance downstream.
    /// </summary>
    [Fact]
    public async Task ShelvedNatives_AreVisibleToTheServeSide()
    {
        var response = await Publish(BuildBundle(withNatives: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var activation = ModuleActivationSidecar.Read(root);
        var (files, _, decline) = ModuleBundleSource.Collect(root, Module, activation);
        Assert.Null(decline);
        Assert.NotEmpty(files);

        var natives = ModuleBundleSource.NativeAssetsOf(root, Module, activation, version: null);
        Assert.Equal(
            [NativePath, SecondNativePath],
            natives.Select(n => n.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            NativeBytes,
            File.ReadAllBytes(natives.Single(n => n.RelativePath == NativePath).FullPath));

        // 🚨 The control that keeps the section SEPARATE: the flat closure is not where the
        // natives went. Every consumer of meshweaver/modules/ filters to entries with no '/' in
        // the remainder, so a native listed there would be silently skipped rather than laid out —
        // which is precisely why the bundle gained a third folder instead of reusing that one.
        Assert.DoesNotContain(files, f =>
            f.EndsWith(".so", StringComparison.Ordinal) || f.EndsWith(".dylib", StringComparison.Ordinal));
    }

    /// <summary>
    /// The generation is CONTENT-ADDRESSED (#3656), and the natives are part of that content: two
    /// bundles differing ONLY in a native payload must land TWO generations. Hashing the
    /// assemblies alone would resolve both to one directory, and the second landing would adopt
    /// the first's engine while its activation entry claimed its own.
    /// </summary>
    [Fact]
    public void TwoLandingsDifferingOnlyInANative_AddressDifferentGenerations()
    {
        var assembly = File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

        var first = ModuleLandingService.GenerationIdOf(
            [(Module + ".dll", assembly)], null, [(NativePath, NativeBytes)]);
        var second = ModuleLandingService.GenerationIdOf(
            [(Module + ".dll", assembly)], null,
            [(NativePath, Encoding.UTF8.GetBytes("ELF-linux-x64-engine-PATCHED"))]);
        var none = ModuleLandingService.GenerationIdOf([(Module + ".dll", assembly)], null);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, none);
    }

    /// <summary>
    /// A bundle that declares NO natives lands its assembly and writes no <c>runtimes</c> tree —
    /// the layout is driven by what the producer packed, never fabricated by the shelf.
    /// </summary>
    [Fact]
    public async Task PublishedModuleWithoutNatives_LandsNoRuntimesTree()
    {
        var response = await Publish(BuildBundle(withNatives: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LandedFile(Module + ".dll") is not null, "the entry assembly did not land");
        Assert.Null(LandedFile(NativePath));
        Assert.Empty(ModuleBundleSource.NativeAssetsOf(
            root, Module, ModuleActivationSidecar.Read(root), version: null));
    }

    /// <summary>
    /// A native declared at a layout the loader does NOT probe is REFUSED at the registry door,
    /// never landed — and named as a 400, not a 500. Bytes at a path nothing looks at read as
    /// shipped and behave as absent, which is the one outcome the section exists to end.
    /// </summary>
    [Fact]
    public async Task ANativeDeclaredOutsideTheProbedLayout_IsRefusedNotLanded()
    {
        var response = await Publish(BuildBundle(
            withNatives: true, nativePath: "runtimes/linux-x64/other/native/libe_sqlite3.so"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(LandedFile(Module + ".dll"));
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

    /// <summary>
    /// #5501 — the endpoint spools the upload instead of buffering it, so a CHUNKED upload (no
    /// <c>Content-Length</c>, the shape a streaming publisher sends) must land exactly as a sized one
    /// does: natives and all, byte-exact.
    /// </summary>
    [Fact]
    public async Task AChunkedPublish_WithNoContentLength_LandsTheSameBundle()
    {
        var response = await Publish(BuildBundle(withNatives: true), chunked: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(LandedFile(Module + ".dll") is not null, "the entry assembly did not land");
        Assert.Equal(NativeBytes, File.ReadAllBytes(LandedFile(NativePath)!));
    }

    /// <summary>
    /// #5501 — the spool is a FILE that holds the body byte-exact, is readable from its start, and is
    /// gone once the request disposes it. A MemoryStream here would be the defect back: the whole
    /// bundle contiguous on the large-object heap, grown by doubling and then copied out.
    /// </summary>
    [Fact]
    public async Task TheUploadSpool_IsAFileThatHoldsTheBodyAndDeletesItself()
    {
        var body = BuildBundle(withNatives: true);
        string spoolPath;
        await using (var spool = await PluginBundleEndpoints.SpoolUploadAsync(
                         new NonSeekableStream(body), CancellationToken.None))
        {
            spoolPath = spool.Name;
            Assert.True(File.Exists(spoolPath), "the upload was not spooled to a file");
            Assert.Equal(0, spool.Position);
            Assert.Equal(body.Length, spool.Length);
            using var copy = new MemoryStream();
            await spool.CopyToAsync(copy);
            Assert.Equal(body, copy.ToArray());
        }
        Assert.False(File.Exists(spoolPath), "the spool outlived the request that owned it");
    }

    /// <summary>A body that cannot seek or report a length — what Kestrel hands a chunked upload.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private async Task<HttpResponseMessage> Publish(byte[] bundle, bool chunked = false)
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
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}")
        {
            Content = chunked
                ? new StreamContent(new NonSeekableStream(bundle))
                : new ByteArrayContent(bundle),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var response = await client.SendAsync(request);
        await app.StopAsync();
        return response;
    }

    /// <summary>
    /// A minimal but REAL module bundle: the same writer the packer uses and the same manifest
    /// record the reader deserializes, so the entry paths and the declaration shape are the
    /// production ones rather than this test's idea of them.
    /// </summary>
    private static byte[] BuildBundle(bool withNatives, string nativePath = NativePath)
    {
        // A REAL managed assembly (#3538): the landing MEASURES the module's link requirements
        // against this platform's surface, and bytes that are not an assembly at all are refused
        // as INDETERMINATE — the fail-closed third state.
        var assembly = File.ReadAllBytes(typeof(BundleReader).Assembly.Location);
        var entries = new List<NuGetPackageWriter.Entry>
        {
            new(NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                () => new MemoryStream(assembly)),
        };
        if (withNatives)
        {
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleNativeEntryPathFor(nativePath),
                () => new MemoryStream(NativeBytes)));
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleNativeEntryPathFor(SecondNativePath),
                () => new MemoryStream(SecondNativeBytes)));
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
                StaticAssets: null)
            {
                NativeAssets = withNatives ? [nativePath, SecondNativePath] : null,
            });

        using var stream = new MemoryStream();
        NuGetPackageWriter.Write(
            stream,
            new MeshWeaver.Plugin.Packaging.PluginManifest(
                Plugin,
                MeshWeaver.Plugin.Packaging.PluginManifest.IdPrefix + Plugin,
                "1.0.0",
                "a module that brings its own engine, for the publish-natives guard",
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
}

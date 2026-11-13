using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Policy <c>major-lines-run-separately</c>, Doc/Architecture/MajorLinesRunSeparately §4b — the
/// plugin registry serves each platform LINE separately. Once MeshWeaver.Plugins <c>main</c> carries
/// v4, an instance whose record says <c>updatePattern: "3.*"</c> must be listed and served the
/// repository at <c>main3</c>, never at <c>main</c>.
///
/// <para>Pinned here, on the core half: (1) a CONSUMER states its line on every registry request —
/// the listing's <c>line</c> query parameter and the files body's <c>line</c> field — read from its
/// record's pattern; (2) a source declaring per-line refs is read, in-process, at the ref of the
/// instance's OWN line, and re-targeted per caller by <see cref="ConfiguredPackageSource.ForLine"/>;
/// (3) nothing declared ⇒ nothing changes: the request is byte-identical to the one sent before lines
/// existed, and every source keeps its configured ref. Real mesh, real client, real HTTP stack; only
/// the registry is a recording handler.</para>
/// </summary>
public class EachPlatformLineReadsItsOwnRefTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RecordingRegistry registry = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false });
                services.AddSingleton<IHttpClientFactory>(new FakeFactory(registry));
                // LAYER the record's pattern onto the host configuration, never replace it.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [PlatformLine.DeclaredPatternConfigKey] = "3.*",
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException("The IConfiguration registration cannot be layered onto.");

    private static readonly PackageManifest Store = new() { Id = "Store", Name = "Store", Version = "1.0.0" };

    /// <summary>
    /// SHOULD-FAIL-IF the client does not state its line: a v3 instance's listing and files requests
    /// carry no <c>line</c>, so the registry cannot tell it from a v4 caller and serves it <c>main</c>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AV3Instance_StatesLine3_OnTheListingAndTheFilesRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new RegistryPackageSource(Mesh, RecordingRegistry.Url, "mwi_key");
        source.Line.Should().Be("3", "the record's updatePattern 3.* names line 3");

        await source.ListPackages("HEAD").Should().Within(TestTimeouts.Convergence).Emit("the listing answers", ct);
        await source.FetchPackage(Store, "HEAD", null).Should().Within(TestTimeouts.Convergence).Emit("the fetch answers", ct);

        registry.ListingQueries.Should().ContainSingle().Which.Should().Be("?ref=HEAD&line=3");
        registry.FilesBodies.Should().ContainSingle().Which.Should().Contain("\"line\":\"3\"");
    }

    /// <summary>
    /// CONTROL — a client that states no line sends exactly what every client sent before lines
    /// existed: the old query and the old body, so a registry that predates lines, and a source that
    /// declares none, see no difference at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Control_AClientStatingNoLine_SendsTheRequestItAlwaysSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new RegistryPackageSource(Mesh, RecordingRegistry.Url, "mwi_key") { Line = null };

        await source.ListPackages("HEAD").Should().Within(TestTimeouts.Convergence).Emit("the listing answers", ct);
        await source.FetchPackage(Store, "HEAD", null).Should().Within(TestTimeouts.Convergence).Emit("the fetch answers", ct);

        registry.ListingQueries.Should().ContainSingle().Which.Should().Be("?ref=HEAD");
        registry.FilesBodies.Should().ContainSingle().Which.Should().Be("{\"id\":\"Store\",\"ref\":\"HEAD\",\"paths\":null}");
    }

    /// <summary>
    /// A registry source that DECLARES <c>{ "3": "main3" }</c> is read in-process at the ref of the
    /// instance's own line, and the registry re-targets it per caller: line 3 → <c>main3</c>, line 4 or
    /// an unstated line → the configured <c>main</c>.
    /// </summary>
    [Fact]
    public void ADeclaredLineRef_IsReadForTheOwnLine_AndRetargetedPerCaller()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            [PlatformLine.DeclaredPatternConfigKey] = "3.*",
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = SourceDirectory,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
            ["PluginCatalog:Sources:0:Ref"] = "main",
            ["PluginCatalog:LineRefs:Plugins:3"] = "main3",
        });

        var source = PackageSources.FromConfiguration(Mesh, config).Should().ContainSingle().Subject;

        source.GitRef.Should().Be("main3", "this instance runs line 3 and the source declares main3 for it");
        source.ConfiguredRef.Should().Be("main");
        source.ForLine("3").GitRef.Should().Be("main3");
        source.ForLine("4").GitRef.Should().Be("main", "line 4 is declared nowhere, so it reads the configured ref");
        source.ForLine(null).GitRef.Should().Be("main");
        source.ForLine("03").GitRef.Should().Be("main3", "a line is a number, compared canonically");
    }

    /// <summary>
    /// CONTROL — a source that declares no line refs keeps its configured ref for every caller and on
    /// every line: today's registry, unchanged.
    /// </summary>
    [Fact]
    public void Control_ASourceDeclaringNoLineRefs_KeepsItsRefForEveryLine()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            [PlatformLine.DeclaredPatternConfigKey] = "3.*",
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = SourceDirectory,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
            ["PluginCatalog:Sources:0:Ref"] = "main",
        });

        var source = PackageSources.FromConfiguration(Mesh, config).Should().ContainSingle().Subject;

        source.GitRef.Should().Be("main");
        source.LineRefs.Should().BeEmpty();
        source.ForLine("3").Should().BeSameAs(source);
        source.ForLine("4").Should().BeSameAs(source);
    }

    /// <summary>
    /// The record's <see cref="MeshWeaver.Deployment.PluginRepoMount.RefForLine"/> and the portal's
    /// <see cref="ConfiguredPackageSource.ForLine"/> canonicalise a line through the ONE rule, so they
    /// answer the same for every spelling of a line.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("03")]
    [InlineData(" 3 ")]
    [InlineData("4")]
    [InlineData(null)]
    [InlineData("three")]
    public void TheRecordAndThePortal_AnswerTheSameRefForEverySpellingOfALine(string? line)
    {
        var mount = new MeshWeaver.Deployment.PluginRepoMount
        {
            Name = "Plugins", Url = "https://github.com/example/plugins", Ref = "main", IsRegistrySource = true,
            LineRefs = ImmutableSortedDictionary<string, string>.Empty.Add("3", "main3"),
        };
        var source = PackageSources.FromConfiguration(Mesh, BuildConfiguration(new Dictionary<string, string?>
        {
            [PlatformLine.ConfigKey] = "4",
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = SourceDirectory,
            ["PluginCatalog:Sources:0:Format"] = "node-repo",
            ["PluginCatalog:Sources:0:Ref"] = "main",
            ["PluginCatalog:LineRefs:Plugins:3"] = "main3",
        })).Should().ContainSingle().Subject;

        source.ForLine(line).GitRef.Should().Be(mount.RefForLine(line));
    }

    /// <summary>The line rule: explicit override, then the pattern's major, then the running major.</summary>
    [Theory]
    [InlineData("4", "3.*", "3.1.0", "4")]
    [InlineData(null, "3.*", "4.0.0", "3")]
    [InlineData(null, "3.1.*", null, "3")]
    [InlineData(null, "4.*", null, "4")]
    [InlineData(null, "*", "3.1.12258", "3")]
    [InlineData(null, null, "4.0.0-ci.1", "4")]
    [InlineData(" ", " ", null, null)]
    [InlineData("three", null, null, null)]
    public void TheLine_IsTheOverrideThenThePatternThenTheRunningMajor(
        string? explicitLine, string? pattern, string? running, string? expected)
        => PlatformLine.Resolve(explicitLine, pattern, running).Should().Be(expected);

    /// <summary>A local directory standing in for the repository — constructing a source reads nothing.</summary>
    private string SourceDirectory
    {
        get
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw-line-refs-" + GetHashCode().ToString("x"));
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>Records the listing queries and files bodies it is sent; answers an empty listing and one file.</summary>
    private sealed class RecordingRegistry : HttpMessageHandler
    {
        public const string Url = "https://registry.test";

        private ImmutableList<string> listingQueries = ImmutableList<string>.Empty;
        private ImmutableList<string> filesBodies = ImmutableList<string>.Empty;

        public ImmutableList<string> ListingQueries => listingQueries;

        public ImmutableList<string> FilesBodies => filesBodies;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Answer(request, cancellationToken));

        private HttpResponseMessage Answer(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri)
                throw new InvalidOperationException(
                    "RecordingRegistry: a request reached the registry with no RequestUri — the client sent nothing to record.");
            var path = uri.AbsolutePath;
            if (path == RegistryPackageSource.RoutePrefix)
            {
                ImmutableInterlocked.Update(ref listingQueries, l => l.Add(uri.Query));
                return Json(PluginRegistryPayloads.List([]));
            }
            if (path == RegistryPackageSource.RoutePrefix + "/files")
            {
                if (request.Content is not { } content)
                    throw new InvalidOperationException(
                        "RecordingRegistry: the files request carried no body — the client must post {id, ref, paths[, line]}.");
                using var reader = new System.IO.StreamReader(content.ReadAsStream(cancellationToken));
                var body = reader.ReadToEnd();
                ImmutableInterlocked.Update(ref filesBodies, l => l.Add(body));
                return Json(PluginRegistryPayloads.Files([new PackageFile("index.json", "{}")]));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

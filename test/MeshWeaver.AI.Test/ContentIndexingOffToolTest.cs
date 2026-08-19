using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.ContentCollections.Indexing.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// #1642 — the <c>search_chunks</c> / <c>get_chunk</c> tools against a mesh where the content-indexing
/// pipeline IS wired but the deployment is NOT configured for it (no embedding provider). That is memex's
/// exact shape: the module is listed in <c>Modules:Assemblies</c>, so <c>AddContentIndexingPipeline</c>
/// runs, while <c>Embedding:Endpoint</c>/<c>Embedding:ApiKey</c> are unset so no
/// <c>IEmbeddingProvider</c> is registered.
///
/// <para>The tools' documented contract is explicit: <i>"When content indexing isn't enabled in this host,
/// returns a <c>{count:0, message:…}</c> envelope rather than erroring."</i> Before the fix the store's
/// registration was NOT gated, so resolving it ran the concrete factory, whose first act
/// (<c>GetRequiredService&lt;IEmbeddingProvider&gt;()</c>) threw — and the MCP caller saw
/// "An error occurred invoking 'search_chunks'", a capability outage that reads as a data bug.</para>
///
/// <para>The store/embedder factories here THROW exactly like the pgvector ones do on an unconfigured
/// deployment, so the test fails if anything on the read path resolves them.</para>
/// </summary>
public class ContentIndexingOffToolTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    /// <summary>The stand-in for "this deployment has no embedding provider" — the exact failure #1642 reported.</summary>
    private static InvalidOperationException Unconfigured() =>
        new("The requested service 'MeshWeaver.Hosting.Embeddings.IEmbeddingProvider' has not been registered.");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddContentIndexingPipeline(
                storeFactory: _ => throw Unconfigured(),
                embedderFactory: _ => throw Unconfigured(),
                enabledWhen: _ => false);

    private Task<string> Run(IObservable<string> op) =>
        op.FirstAsync().Timeout(TimeSpan.FromSeconds(45)).ToTask(Ct);

    [Fact(Timeout = 60000)]
    public void UnconfiguredPipeline_ResolvingTheStore_DoesNotThrow()
    {
        var store = Mesh.ServiceProvider.GetService<IChunkedContentVectorStore>();
        store.Should().BeAssignableTo<IInertContentIndex>(
            "a wired-but-unconfigured pipeline must resolve an inert stand-in — the concrete factory " +
            "(which needs an IEmbeddingProvider) must never run");
        Mesh.ServiceProvider.GetActiveChunkStore().Should().BeNull(
            "the availability helper maps the stand-in back to the null every consumer branches on");
    }

    [Fact(Timeout = 60000)]
    public async Task SearchChunks_Anchored_ReturnsEmptyEnvelopeWithMessage()
    {
        var json = await Run(ChunkNavigation.SearchChunks(
            Mesh.ServiceProvider, "accrued benefit obligation", TestPartition));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
        var message = doc.RootElement.GetProperty("message").GetString();
        message.Should().Contain("not active on this deployment",
            "a switched-off capability must say it is switched off, not fail opaquely");
        message.Should().Contain("Embedding:Endpoint",
            "the message must name what to configure — otherwise the next reader hunts a data bug");
    }

    [Fact(Timeout = 60000)]
    public async Task SearchChunks_NamespaceGrammar_ReturnsEmptyEnvelopeWithMessage()
    {
        var json = await Run(ChunkNavigation.SearchChunks(
            Mesh.ServiceProvider,
            $"namespace:{TestPartition}/content scope:subtree accrued benefit obligation",
            scopePath: null));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("message").GetString().Should().Contain("Embedding:Endpoint");
    }

    [Fact(Timeout = 60000)]
    public async Task GetChunk_ReturnsNotAvailableEnvelope()
    {
        var json = await Run(ChunkNavigation.GetChunk(
            Mesh.ServiceProvider, $"{TestPartition}/content", "docs/note.txt", 0));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.TryGetProperty("text", out _).Should().BeFalse(
            "an unavailable index must not hand back chunk text");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("not active on this deployment");
    }
}

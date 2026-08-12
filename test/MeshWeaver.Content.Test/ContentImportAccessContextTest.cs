using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Regression pin for the content-file import losing the CALLER'S IDENTITY
/// (MeshWeaver.Reinsurance#46, "Demo setup loading failed"): the demo wrote all 412 of its nodes
/// and had all 409 of its attachment groups REFUSED —
/// <c>"AccessContext must never be null for an application post … message=SyncContentFilesRequest"</c>.
///
/// <para><b>The defect.</b> Every other framework write primitive (<c>IMeshService.CreateNode</c> and
/// friends, <c>MeshNodeStreamHandle.Update</c>) snapshots <c>AccessService.Context</c> EAGERLY, on the
/// caller's thread, and pins it on the delivery. The two content-import builders did neither: their
/// <c>Post()</c> was a bare <c>hub.Observe(request, o =&gt; o.WithTarget(address))</c> inside an
/// <c>Observable.Defer</c>, so the identity was read at SUBSCRIBE time — from whatever thread the
/// pipeline happened to subscribe on. In any real import that is a <c>Concat</c>/<c>Merge</c> pump or a
/// storage emission thread, where the caller's (or an <c>ImpersonateAsSystem</c>) <c>AsyncLocal</c> is
/// gone. Node writes therefore landed and content writes were failed closed, in the same run, from the
/// same call site — the asymmetry the issue reports.</para>
///
/// <para><b>What this test pins.</b> The write is BUILT with a specific caller ambient and SUBSCRIBED on
/// another thread that does not carry that identity. The identity that reaches the wire must still be
/// the caller's — not the ambient identity of the subscribing thread (here the xUnit host's standing
/// DevLogin admin, which <c>CircuitContext</c> resolves to on every thread). A single-threaded version
/// of this test would pass with or without the fix and prove nothing: the thread hop IS the defect.</para>
/// </summary>
public class ContentImportAccessContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan StepTimeout = 30.Seconds();

    /// <summary>
    /// The caller. Deliberately NOT <see cref="TestUsers.Admin"/> ("Roland"), the xUnit host's standing
    /// identity: <c>CircuitContext</c> falls back to it on any thread with no context of its own, so
    /// "the caller" and "whatever the subscribing thread carries" stay distinguishable.
    /// </summary>
    private static readonly AccessContext Importer = new()
    {
        ObjectId = "content-importer",
        Name = "Content Importer"
    };

    // Not valid UTF-8 — the pack documents an import attaches are binaries.
    private static readonly byte[] PackDocument =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x21];

    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "ContentImportAccessContextTest", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The identity each outgoing <see cref="SyncContentFilesRequest"/> delivery actually carries.
    /// Recorded by a post-pipeline step on the issuing hub AFTER the rest of the chain has run
    /// (<c>AddPipeline</c> nests outer-first, so recording the value <c>next(d)</c> RETURNS is what
    /// sees the final resolved identity — exactly what the receiving node hub will authorise against).
    /// A <see cref="ReplaySubject{T}"/> so the assertion can attach after the post; an instance field,
    /// never static.
    /// </summary>
    private readonly ReplaySubject<string?> _stampedIdentities = new();

    /// <summary>The read-only source collection the collection→collection import copies FROM.</summary>
    private string SourceDir => Path.Combine(_contentRoot, "_source");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(SourceDir);
        File.WriteAllBytes(Path.Combine(SourceDir, "asset.bin"), PackDocument);

        // base.ConfigureMesh adds PublicAdminAccess, so BOTH candidate identities are authorised to
        // write. Authorisation is not what this test discriminates on — attribution is.
        return base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(config => config
                .AddContentCollections()
                .AddFileSystemContentCollection("ImportSource", _ => SourceDir)
                // Mirror the portal: a per-node writable "content" collection on disk.
                .AddContentCollection(_ => new ContentCollectionConfig
                {
                    Name = ContentCollectionsExtensions.DefaultCollectionName,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(_contentRoot, config.Address.ToString()),
                    IsEditable = true,
                    ExposeInChildren = true
                }));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_contentRoot))
        {
            try { Directory.Delete(_contentRoot, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    [Fact(Timeout = 60000)]
    public async Task SyncContentFiles_CarriesTheCallersIdentity_WhenSubscribeLandsOnAnotherThread()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(new MeshNode("CtxSpace") { NodeType = "Space", Name = "Ctx Space" })
                .Should().Within(StepTimeout).Emit();
        }

        var client = GetClient(c => ConfigureClient(c)
            .AddPostPipeline(p => p.AddPipeline((delivery, next) =>
            {
                var posted = next(delivery);
                if (posted.Message is SyncContentFilesRequest)
                    _stampedIdentities.OnNext(posted.AccessContext?.ObjectId);
                return posted;
            })));

        // BUILD the write with the caller ambient — the moment the framework must snapshot.
        IObservable<ImportContentResponse> write;
        using (access.SwitchAccessContext(Importer))
        {
            write = client.SyncContentFiles("CtxSpace")
                .To(ContentCollectionsExtensions.DefaultCollectionName, "packs")
                .Add("pack.bin", PackDocument)
                .Mirror(false)
                .Post();
        }

        // SUBSCRIBE from another thread. The scope above is disposed, so this thread carries no
        // Context of its own — precisely the pump/emission thread the production pipeline subscribes
        // on. The post happens HERE (the observable is cold), which is why a lazy read loses the user.
        var ct = TestContext.Current.CancellationToken;
        string? ambientAtSubscribe = null;
        string? identityInsideSubscriberCallback = null;
        var response = await Task.Run(() =>
        {
            ambientAtSubscribe = access.Context?.ObjectId;
            return write
                .Do(_ => identityInsideSubscriberCallback = access.Context?.ObjectId)
                .FirstAsync()
                .Timeout(StepTimeout)
                .ToTask(ct);
        }, ct);

        ambientAtSubscribe.Should().NotBe(Importer.ObjectId,
            because: "the hop must be real — if the caller's AsyncLocal still flowed to the " +
                     "subscribing thread the test would pass with or without the fix and prove " +
                     "nothing about the capture");

        var stamped = await _stampedIdentities.FirstAsync().Timeout(StepTimeout).ToTask(ct);
        stamped.Should().Be(Importer.ObjectId,
            because: "SyncContentFilesBuilder.Post captures the caller EAGERLY and pins it on the " +
                     "delivery, so the owning node hub authorises the write as the caller. Without " +
                     $"that capture the post carried the subscribing thread's ambient identity " +
                     $"('{TestUsers.Admin.ObjectId}' here, a null AccessContext in production — the " +
                     "failed-closed delivery of MeshWeaver.Reinsurance#46)");

        identityInsideSubscriberCallback.Should().Be(Importer.ObjectId,
            because: "the returned observable is CarryAccessContext-wrapped, so a caller chaining " +
                     "further work inside its Subscribe callback still runs as itself — the same " +
                     "contract every MeshService write primitive honours");

        response.Success.Should().BeTrue($"the content sync must land (error: {response.Error})");
        response.FilesImported.Should().Be(1, "the one supplied pack document is written");

        var landed = Directory.GetFiles(_contentRoot, "pack.bin", SearchOption.AllDirectories);
        landed.Should().HaveCount(1, "the attachment lands in the node's content collection");
        File.ReadAllBytes(landed[0]).SequenceEqual(PackDocument)
            .Should().BeTrue("the bytes are written stream-to-stream, not through the text API");
    }

    /// <summary>The identity each outgoing <c>CreateOrUpdateNodeRequest</c> delivery carries.</summary>
    private readonly ReplaySubject<string?> _nodeStamps = new();

    /// <summary>
    /// A SECOND identity, distinct from both <see cref="Importer"/> and the host's standing admin, so
    /// "the declared value is what shipped" cannot be confused with "something ambient happened to
    /// match".
    /// </summary>
    private static readonly AccessContext Attacher = new()
    {
        ObjectId = "pack-attacher",
        Name = "Pack Attacher"
    };

    /// <summary>
    /// The ambient <c>AccessContext</c> observed while the pipeline's SECOND stage builds its posts —
    /// the exact place MeshWeaver.Reinsurance#46 lost the identity. Recorded, then asserted on: the
    /// test is only meaningful while that ambient is NOT the enclosing scope's.
    /// </summary>
    private readonly ReplaySubject<string?> _secondStageAmbient = new();

    /// <summary>
    /// 🚨 The whole of MeshWeaver.Reinsurance#46, pinned: an import that writes NODES and then
    /// attaches FILES, under ONE enclosing impersonation scope, must post BOTH under a real identity.
    ///
    /// <para><b>Why the demo's shape is the test's shape.</b> The importer is
    /// <c>Observable.Using(() =&gt; access.ImpersonateAsSystem(), _ =&gt; nodeWrites.Concat(fileWrites))</c>.
    /// An <c>AsyncLocal</c> scope opened at Subscribe covers only what runs synchronously on that
    /// thread — the FIRST stage. The second stage is subscribed from the first's completion, on a
    /// pool/hub thread, and everything it builds there (its LINQ projection AND its <c>Defer</c>
    /// bodies) reads a NULL ambient. Measured on this very pipeline before the fix:
    /// <c>nodeProjection[0..2] ambient=content-importer thread=13</c> · <c>projection[0..2]
    /// ambient=(null) thread=19</c>. So all 412 node writes landed and all 409 attachment groups were
    /// failed closed — "AccessContext must never be null for an application post". The split was never
    /// about node-vs-file; it was about WHICH STAGE built the post.</para>
    ///
    /// <para><b>Why eager capture (#1263) did not fix it.</b> Eager means "at <c>Post()</c>" — and
    /// here <c>Post()</c> is itself called on the pump thread, so it eagerly captures nothing. The
    /// cure is to stop reading the ambient at all: declare the identity as a VALUE
    /// (<c>WithAccessContext</c>/<c>ImpersonateAsSystem</c> on the builder), which no thread hop and
    /// no later re-ordering of a Subscribe can lose.</para>
    ///
    /// <para><b>Why the assertions are on IDENTITY, never on "it landed".</b> The xUnit host sets a
    /// standing <c>hostIdentity</c>, so <c>CircuitContext</c> resolves to the DevLogin admin on every
    /// thread: unfixed, this pipeline still wrote all six files — as "Roland" — and any
    /// landed/Success assertion passed. Only the stamped ObjectId separates the fix from the bug.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DeclaredIdentity_SurvivesAPipelineStageTheAmbientScopeNeverReaches()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(new MeshNode("DemoSpace") { NodeType = "Space", Name = "Demo Space" })
                .Should().Within(StepTimeout).Emit();
        }

        var client = GetClient(c => ConfigureClient(c)
            .AddPostPipeline(p => p.AddPipeline((delivery, next) =>
            {
                var posted = next(delivery);
                if (posted.Message is SyncContentFilesRequest)
                    _stampedIdentities.OnNext(posted.AccessContext?.ObjectId);
                else if (posted.Message is CreateOrUpdateNodeRequest)
                    _nodeStamps.OnNext(posted.AccessContext?.ObjectId);
                return posted;
            })));
        var mesh = client.ServiceProvider.GetRequiredService<IMeshService>();

        // === STAGE 1: node writes. Built synchronously at the outer Subscribe, i.e. INSIDE the
        // scope, so MeshService's eager capture snapshots the impersonated identity. ===
        var nodeWrites = Enumerable.Range(0, 3)
            .Select(i => mesh
                .CreateOrUpdateNode(new MeshNode($"Child{i}", "DemoSpace")
                {
                    NodeType = "Markdown",
                    Name = $"Child {i}"
                })
                .Take(1)
                .Timeout(StepTimeout)
                .Select(_ => true)
                .Catch((Exception e) =>
                {
                    Output.WriteLine($"node {i} failed: {e.Message}");
                    return Observable.Return(false);
                }))
            .ToObservable()
            .Concat();

        // === STAGE 2: file writes. Built on the thread that completes stage 1 — outside the scope.
        // Declaring the identity is what makes it survive; nothing here may read the ambient. ===
        var fileWrites = Enumerable.Range(0, 3)
            .Select(i =>
            {
                _secondStageAmbient.OnNext(access.Context?.ObjectId);
                return Observable.Defer(() =>
                    {
                        _secondStageAmbient.OnNext(access.Context?.ObjectId);
                        return client.SyncContentFiles("DemoSpace")
                            .To(ContentCollectionsExtensions.DefaultCollectionName, $"grp{i}")
                            .Add("pack.bin", PackDocument)
                            .Mirror(false)
                            .WithAccessContext(Attacher)
                            .Post();
                    })
                    .Take(1)
                    .Timeout(StepTimeout)
                    .Select(r => r.Success)
                    .Catch((Exception e) =>
                    {
                        Output.WriteLine($"files {i} failed: {e.Message}");
                        return Observable.Return(false);
                    });
            })
            .ToObservable()
            .Concat();

        var ct = TestContext.Current.CancellationToken;
        var results = await Task.Run(() => Observable.Using(
                    () => access.SwitchAccessContext(Importer),
                    _ => nodeWrites.Concat(fileWrites).ToList())
                .Timeout(StepTimeout)
                .FirstAsync()
                .ToTask(ct), ct);

        var nodeStamp = await _nodeStamps.FirstAsync().Timeout(StepTimeout).ToTask(ct);
        var fileStamp = await _stampedIdentities.FirstAsync().Timeout(StepTimeout).ToTask(ct);
        // The run is over, so the sample set is complete: close the subject and take ALL of it.
        // Asserting only the first sample would let a later one silently see the enclosing scope.
        _secondStageAmbient.OnCompleted();
        var stage2Ambients = await _secondStageAmbient.ToList().Timeout(StepTimeout).ToTask(ct);
        Output.WriteLine($"ok={results.Count(r => r)}/{results.Count} nodeStamp={nodeStamp} " +
                         $"fileStamp={fileStamp} " +
                         $"stage2Ambients=[{string.Join(", ", stage2Ambients.Select(a => a ?? "(null)"))}]");

        stage2Ambients.Should().NotBeEmpty("the second stage must actually have built its posts");
        stage2Ambients.Should().NotContain(Importer.ObjectId,
            because: "the hop must be real, at EVERY sample. The enclosing Observable.Using scope " +
                     "covers only the first stage; if any part of the second stage could see it, this " +
                     "test would pass whether or not the identity is declared and would pin nothing. " +
                     "That escape IS the defect — in production the ambient here is null and every " +
                     "post built here is refused");

        nodeStamp.Should().Be(Importer.ObjectId,
            because: "stage one is built inside the scope, so MeshService's eager capture pins the " +
                     "impersonated caller — the half of MeshWeaver.Reinsurance#46 that always worked");

        fileStamp.Should().Be(Attacher.ObjectId,
            because: "the sync declares its identity as a VALUE, so the delivery carries it no matter " +
                     "which thread built the post. Reading the ambient instead yields null in " +
                     $"production (and the host's standing '{TestUsers.Admin.ObjectId}' here) — the " +
                     "409 refused attachment groups of MeshWeaver.Reinsurance#46");

        results.Should().AllSatisfy(r => r.Should().BeTrue("every write in the run must land"));
        Directory.GetFiles(_contentRoot, "pack.bin", SearchOption.AllDirectories)
            .Should().HaveCount(3, "each attachment group writes its pack document");
    }

    [Fact(Timeout = 60000)]
    public async Task ImportContent_CarriesTheCallersIdentity_WhenSubscribeLandsOnAnotherThread()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await NodeFactory.CreateNode(new MeshNode("ImpSpace") { NodeType = "Space", Name = "Imp Space" })
                .Should().Within(StepTimeout).Emit();
        }

        var client = GetClient(c => ConfigureClient(c)
            .AddPostPipeline(p => p.AddPipeline((delivery, next) =>
            {
                var posted = next(delivery);
                if (posted.Message is ImportContentRequest)
                    _stampedIdentities.OnNext(posted.AccessContext?.ObjectId);
                return posted;
            })));

        IObservable<ImportContentResponse> import;
        using (access.SwitchAccessContext(Importer))
        {
            import = client.ImportContent("ImpSpace")
                .From("ImportSource")
                .To(ContentCollectionsExtensions.DefaultCollectionName, "imported")
                .Post();
        }

        var ct = TestContext.Current.CancellationToken;
        var response = await Task.Run(
            () => import.FirstAsync().Timeout(StepTimeout).ToTask(ct), ct);

        var stamped = await _stampedIdentities.FirstAsync().Timeout(StepTimeout).ToTask(ct);
        stamped.Should().Be(Importer.ObjectId,
            because: "ContentImportBuilder.Post carries the same eager capture — the collection→" +
                     "collection import is the second half of the same defect, and a caller that " +
                     "subscribes off its own thread must still import as itself");

        response.Success.Should().BeTrue($"the collection import must land (error: {response.Error})");
    }
}

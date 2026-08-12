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

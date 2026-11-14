using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A timed-out upload says WHICH step timed out.</b> <see cref="MeshOperations.Upload"/> has
/// two awaited legs to the owning node: the collection-config lookup, and the write delivery that
/// carries the bytes (<c>SaveContentFileRequest</c>, inline or as a staged handle). Its fault handler
/// used to map EVERY <see cref="TimeoutException"/> to <i>"the node … did not respond to the
/// content-collection lookup in time — confirm the path resolves to a content-enabled node"</i>.
///
/// <para>Measured on the control instance on 2026-11-13: eight <c>SaveAttachment</c> calls of a
/// 4.9 MB document into an editable <c>content</c> collection each answered that sentence after
/// 30–45 s, while the SAME file uploaded through the web UI into the SAME collection landed at once,
/// and a 251 KB file had been saved through the same tool two days earlier. The sentence sent the
/// investigation to the collection configuration, which the manual upload had already exonerated.
/// A write that stalls is a different fact with a different remedy — and, unlike a lookup that never
/// answered, it may have landed.</para>
///
/// <para>The owner here declares the collection and ANSWERS the lookup, but swallows the write
/// delivery for one folder; the client's request deadline is shortened so the write leg times out in
/// seconds. Both sizes are driven — under one delivery's budget (inline) and over it (out of band) —
/// because the out-of-band leg is the one the production failure ran. The control is the same upload
/// to an ordinary folder, which must succeed: otherwise the lookup itself is broken and the
/// assertion about the write leg means nothing.</para>
/// </summary>
public class AnUploadTimeoutNamesTheStepThatTimedOutTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeType = "UploadTimeoutProbe";
    private const string Target = "UploadTimeoutTarget";
    private const string SwallowedFolder = "swallowed";

    /// <summary>Shorter than any lookup bound, so only the write leg can be the one that times out.</summary>
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "UploadTimeout", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        return base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(NodeType)
                {
                    Name = "Upload Timeout Probe",
                    HubConfiguration = config => config
                        // Ahead of every handler: the write delivery is consumed and never
                        // answered — the owner went silent on the write, after answering the lookup.
                        .AddDeliveryPipeline(p => p.AddPipeline((delivery, ct, next) =>
                            delivery.Message is SaveContentFileRequest { FilePath: var file }
                            && file.StartsWith(SwallowedFolder + "/", StringComparison.Ordinal)
                                ? Observable.Return(delivery.Processed())
                                : next.Invoke(delivery, ct)))
                        .AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = ContentCollectionsExtensions.DefaultCollectionName,
                            SourceType = "FileSystem",
                            BasePath = _contentRoot,
                            IsEditable = true,
                            ExposeInChildren = true,
                        }),
                },
                new MeshNode(Target) { Name = "Upload Timeout Target", NodeType = NodeType });
    }

    private IMessageHub ShortDeadlineClient()
        => GetClient(c => ConfigureClient(c).WithRequestTimeout(ClientRequestTimeout));

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(4711).NextBytes(bytes);
        return bytes;
    }

    private static Task<string> Upload(IMessageHub client, string path, byte[] bytes, CancellationToken cancellationToken)
        => new MeshOperations(client).Upload(path, bytes)
            .Catch<string, Exception>(ex => Observable.Return($"FAULT {ex.GetType().Name}: {ex.Message}"))
            .Take(1)
            .Await(cancellationToken);

    /// <summary>Under (inline) and over (staged out of band) one delivery's 1 MiB budget.</summary>
    public static TheoryData<int> Sizes => [4_096, 3 * 1024 * 1024];

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Sizes))]
    public async Task TheControl_AnUploadTheOwnerAnswers_IsWritten(int size)
    {
        var bytes = Bytes(size);
        var answer = await Upload(ShortDeadlineClient(), $"{Target}/content/kept/file-{size}.bin", bytes,
            TestContext.Current.CancellationToken);
        Output.WriteLine(answer);

        answer.Should().Contain("\"Uploaded\"",
            "the lookup and the write both work on this node — without that, the case below proves nothing");
        (await File.ReadAllBytesAsync(Path.Combine(_contentRoot, "kept", $"file-{size}.bin"),
                TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Sizes))]
    public async Task AWriteTheOwnerNeverAnswers_IsNotReportedAsAFailedLookup(int size)
    {
        var answer = await Upload(ShortDeadlineClient(), $"{Target}/content/{SwallowedFolder}/file-{size}.bin", Bytes(size),
            TestContext.Current.CancellationToken);
        Output.WriteLine(answer);

        answer.Should().StartWith("Error:");
        answer.Should().NotContain("did not respond to the content-collection lookup",
            "the lookup ANSWERED — the owner declared an editable collection; blaming it sends the "
            + "reader to a configuration that is fine");
        answer.Should().Contain("did not answer the write",
            "the step that timed out is the write delivery, and the answer has to say so");
        answer.Should().Contain("may or may not have been written",
            "a write that went unanswered may still have landed — the caller must read the target back");
        answer.Should().NotContain("Nothing was written",
            "only a refusal decided before the write may claim nothing was written");
        answer.Should().Contain(size > ContentDeliveryBudget.BudgetBytes / 2 ? "out of band" : "inline",
            "which transport carried the bytes is the first thing an operator needs to know");
    }
}

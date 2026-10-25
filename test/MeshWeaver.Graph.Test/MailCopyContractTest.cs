using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The provider-neutral mail copy lives in core (it was born in the Mail module). Stored copies and the
/// Executive Assistant's StoredPath contract outlive the move, so what is PINNED here is the wire: the
/// type names ARE the <c>$type</c> discriminators, a node written before attachments existed still reads,
/// and an attachment list survives a write and a read on a real mesh. A change to a name or a property
/// spelling below breaks every stored copy.
/// </summary>
public class MailCopyContractTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    // Called TWICE on purpose: the Mail module and every other provider module call it and any one of them may
    // be the only one installed — the mesh must start with each node type seeded once (the write below proves it).
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddMailCopyTypes().AddMailCopyTypes();

    // A copy exactly as the Mail module stored it BEFORE attachments existed (camelCase, discriminator first).
    private const string LegacyCopy = """
        {"$type":"MailMessageCopy","provider":"outlook","messageId":"AAMk-1","generation":"g1","subject":"Quarterly figures",
         "from":"Max Muster","fromAddress":"max@fabrikam.example","receivedAt":"2026-10-14T07:30:00+00:00","receivedTicks":1760427000000,
         "orderKey":1760427000000123,"isRead":true,"webLink":"https://outlook.office365.com/owa/?ItemID=1","conversationId":"conv-1",
         "preview":"Please find","body":"Please find the numbers.","copiedAt":"2026-10-16T12:00:00+00:00"}
        """;

    private const string LegacySource = """
        {"$type":"MailCopySource","provider":"outlook","generation":"g1","enabledAt":"2026-10-16T12:00:00+00:00",
         "mailboxId":"tid/oid","windowStart":"2026-07-18T12:00:00+00:00","cursor":"https://graph.microsoft.com/v1.0/delta?x=1","backfilled":true}
        """;

    [Fact]
    public void TheTypeNames_AreTheStoredDiscriminators()
    {
        MailCopyNodeTypes.SourceNodeType.Should().Be("MailCopySource");
        MailCopyNodeTypes.MessageNodeType.Should().Be("MailMessageCopy");
        MailCopyNodeTypes.Segment.Should().Be("_MailCopy");
        MailCopyNodeTypes.CopyPath("jroe", "outlook", "g1", "AAMk-1").Should().StartWith("jroe/_MailCopy/outlook/");
    }

    [Fact]
    public void ACopyStoredBeforeAttachments_StillReads_AndRoundTripsItsDiscriminator()
    {
        var read = JsonSerializer.Deserialize<object>(LegacyCopy, Mesh.JsonSerializerOptions);

        var copy = read.Should().BeOfType<MailMessageCopy>().Subject;
        (copy.Provider, copy.MessageId, copy.Generation, copy.Subject, copy.IsRead, copy.OrderKey)
            .Should().Be(("outlook", "AAMk-1", "g1", "Quarterly figures", true, 1760427000000123L));
        copy.Attachments.Should().BeNull("undecided — the field did not exist");
        copy.HasAttachments.Should().BeNull();
        var written = JsonSerializer.Serialize<object>(copy, Mesh.JsonSerializerOptions);
        using var doc = JsonDocument.Parse(written);
        doc.RootElement.GetProperty("$type").GetString().Should().Be("MailMessageCopy");
        doc.RootElement.GetProperty("messageId").GetString().Should().Be("AAMk-1");
        doc.RootElement.TryGetProperty("attachments", out var attachments).Should().BeFalse(
            $"a null list is not written, so a stored copy is unchanged by a round trip (was {attachments})");
    }

    [Fact]
    public void ASourceStoredBeforeTheProjection_StillReads_AsProjectionZero()
    {
        var source = JsonSerializer.Deserialize<object>(LegacySource, Mesh.JsonSerializerOptions)
            .Should().BeOfType<MailCopySource>().Subject;

        (source.Provider, source.Generation, source.MailboxId, source.Backfilled).Should().Be(("outlook", "g1", "tid/oid", true));
        source.Projection.Should().Be(0, "0 is a copy made before attachments");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAttachmentList_SurvivesAWriteAndARead_OnAMesh()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = $"user{Guid.NewGuid():N}"[..12];
        await SeedTopLevel(MeshNode.FromPath(owner) with
        {
            Name = "Jane Roe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "jane@acme.example", FullName = "Jane Roe" },
        }).WaitAsync(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        var path = MailCopyNodeTypes.CopyPath(owner, "outlook", "g1", "m1");
        var copy = JsonSerializer.Deserialize<object>(LegacyCopy, Mesh.JsonSerializerOptions).Should().BeOfType<MailMessageCopy>().Subject with
        {
            MessageId = "m1",
            Attachments =
            [
                new MailAttachmentCopy { AttachmentId = "att-1", Name = "Report Q3.pdf", Kind = "file", Size = 5, StoredPath = $"{path}/attachments/ab/Report Q3.pdf" },
                new MailAttachmentCopy { AttachmentId = "att-2", Name = "logo.png", IsInline = true, SkippedReason = "inline" },
            ],
        };

        await store.Upsert(owner, copy).Timeout(Bound).Await(ct);

        var read = (await store.Read(owner, "outlook", "g1", "m1").Timeout(Bound).Await(ct))!;
        read.Attachments.Should().HaveCount(2);
        read.Attachments![0].StoredPath.Should().Be($"{path}/attachments/ab/Report Q3.pdf", "the contract the Executive Assistant reads");
        read.Attachments[1].SkippedReason.Should().Be("inline");
        var node = (await Mesh.GetMeshNode(path, Bound).Timeout(Bound).Await(ct))!;
        node.NodeType.Should().Be("MailMessageCopy");
    }
}

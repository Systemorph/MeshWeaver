using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The two listings behind "a copy whose account is gone can still be deleted" and "a restart resumes every enabled
/// copy": the owner's own persisted keys, and the installation's enabled copies across owners.
/// </summary>
public class MailCopySourceListingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder).AddMailCopyTypes();

    private async Task<string> SeedOwner(CancellationToken ct)
    {
        var owner = $"user{Guid.NewGuid():N}"[..12];
        await SeedTopLevel(MeshNode.FromPath(owner) with
        {
            Name = "Max Muster",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "max@fabrikam.example", FullName = "Max Muster" },
        }).WaitAsync(ct);
        return owner;
    }

    [Fact(Timeout = 120_000)]
    public async Task TheOwner_ListsItsPersistedCopies_AndAnotherPersonCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        await store.Create(owner, new MailCopySource { Provider = "outlook.acct-1", Generation = "g1" }).Timeout(Bound).Await(ct);

        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            (await store.SourceKeys(owner).Timeout(Bound).Await(ct)).Order().Should().Equal("outlook", "outlook.acct-1");

        Task<System.Collections.Immutable.ImmutableList<string>> asOther;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
            asOther = store.SourceKeys(owner).Timeout(Bound).Await(ct);
        (await Record.ExceptionAsync(() => asOther)).Should().BeOfType<UnauthorizedAccessException>("the list names the owner's accounts");
    }

    [Fact(Timeout = 120_000)]
    public async Task TheInstallation_ListsEveryEnabledCopyAcrossOwners_AsPlatformWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await SeedOwner(ct);
        var b = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        await store.Create(a, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);
        await store.Create(b, new MailCopySource { Provider = "gmail", Generation = "g1" }).Timeout(Bound).Await(ct);

        var all = await store.EnabledSources().Timeout(Bound).Await(ct);

        all.Should().Contain(new MailCopySourceRef(a, "outlook")).And.Contain(new MailCopySourceRef(b, "gmail"));
    }

    /// <summary>
    /// The owner's listing is an ENUMERATION, never a page: a key clipped off it is a copy whose account is gone that
    /// Settings can no longer show for deletion. It was bounded at 50 with no continuation (review of #7154).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheOwnersListing_ReturnsEveryPersistedCopy_PastAnyPageSize()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedOwner(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var keys = Enumerable.Range(0, 51).Select(i => $"outlook.acct-{i:D2}").ToArray();
        foreach (var key in keys)
            await store.Create(owner, new MailCopySource { Provider = key, Generation = "g1" }).Timeout(Bound).Await(ct);

        using (access.SwitchAccessContext(new AccessContext { ObjectId = owner, Name = owner }))
            (await store.SourceKeys(owner).Timeout(Bound).Await(ct)).Order().Should().Equal(
                keys, "every persisted copy is listed — the listing was clipped at 50 with no way to page on");
    }
    /// <summary>
    /// A listing frame that is only a FLOOR — a provider silent, or one that dropped part of its read — is a fault,
    /// never the list: read as complete it hides copies from Settings and from restart recovery (review of #7154).
    /// </summary>
    [Theory]
    [InlineData("silent")]
    [InlineData("incomplete")]
    [InlineData("complete")]
    public void AListingFrameThatIsOnlyAFloor_IsRejected_NeverReadAsTheWholeList(string shape)
    {
        var change = new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = [MeshNode.FromPath("owner/_MailCopy/outlook")],
            Timestamp = DateTimeOffset.UtcNow,
            SilentProviders = shape == "silent" ? ["postgres"] : null,
            SnapshotIncomplete = shape == "incomplete",
        };
        Exception? fault = null;
        QueryResultChange<MeshNode>? passed = null;
        using var _ = MeshMailCopyStore.RejectIncompleteListing(change, "SourceKeys")
            .Subscribe(c => passed = c, ex => fault = ex);

        if (shape == "complete")
        {
            fault.Should().BeNull("a frame every provider answered in full is the listing");
            passed.Should().BeSameAs(change);
        }
        else
        {
            passed.Should().BeNull($"a {shape} frame is a floor, and a floor must never be read as the whole list");
            fault.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("INCOMPLETE");
        }
    }
}

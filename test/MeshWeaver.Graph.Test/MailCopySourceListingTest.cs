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
}

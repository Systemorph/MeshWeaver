using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 A MAPPED per-node collection is listed by <c>GetAllCollectionConfigs</c> — the portal's Files
/// tab, the MCP collection listing and the autocomplete all read that list (triage fb-5639c3b3:
/// <c>Posts/AzureBellCurveMeme/Files</c> rendered only its Back button, while an upload addressed to
/// the collection BY NAME worked).
///
/// <para>The mapping declares itself child-visible (<c>MapContentCollection</c>: "writable per-node
/// view"), but the resolved config was built as <c>sourceConfig with {…}</c> and so inherited the
/// SOURCE's <c>ExposeInChildren</c> — and the portal registers its <c>storage</c> backing store
/// hidden (<c>false</c>) on purpose. The list filters on that flag, so every mapped collection
/// vanished from it while named lookups kept working. The fixture registers <c>storage</c> exactly
/// as the portal does: hidden.</para>
/// </summary>
public class MappedCollectionExposureTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string _storagePath = Path.Combine(AppContext.BaseDirectory, "Files", "MappedExposure");

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => configuration
            .AddContentCollections()
            // The portal's backing store: a read-only source, HIDDEN from children by design.
            .AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = "storage",
                SourceType = "FileSystem",
                BasePath = _storagePath,
                IsEditable = false,
                ExposeInChildren = false,
                Settings = new System.Collections.Generic.Dictionary<string, string> { ["BasePath"] = _storagePath }
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration
            .AddContentCollections()
            .MapContentCollection("attachments", "storage", "attachments/node1");

    [Fact]
    public void AMappedCollection_IsListed_WhileItsHiddenBackingStoreIsNot()
    {
        var node = GetClient().ServiceProvider.GetRequiredService<IContentService>();

        var all = node.GetAllCollectionConfigs();

        var mapped = all.SingleOrDefault(c => c.Name == "attachments");
        mapped.Should().NotBeNull("the mapping declares itself child-visible, so the listing must carry it");
        mapped!.ExposeInChildren.Should().BeTrue();
        mapped.IsEditable.Should().BeTrue("the mapping's writable flag still wins over the read-only source");
        mapped.BasePath.Should().EndWith(Path.Combine("attachments", "node1"));

        // NEGATIVE CONTROL: the backing store the mapping resolves against stays hidden.
        all.Should().NotContain(c => c.Name == "storage",
            "the source is a hidden backing store; exposing the mapping must not expose it");
    }

    [Fact]
    public void ANamedLookup_StillResolvesTheMappedCollection()
    {
        var node = GetClient().ServiceProvider.GetRequiredService<IContentService>();

        var byName = node.GetCollectionConfig("attachments");

        byName.Should().NotBeNull();
        byName!.ExposeInChildren.Should().BeTrue("the named lookup and the listing must agree");
    }

    /// <summary>The ambient identity a portal circuit carries when the browser asks (a post carries one or is refused).</summary>
    private IDisposable AsViewer() =>
        Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>()
            .SwitchAccessContext(new MeshWeaver.Messaging.AccessContext { ObjectId = "viewer", Name = "Viewer" });

    [Fact(Timeout = 30000)]
    public async Task TheOwnersConfiguration_IsFetchedByNameFromTheHubThatDeclaresIt()
    {
        var owner = GetClient().Address;
        using var _ = AsViewer();

        var declared = await Mesh.GetOwnerCollectionConfig(owner, "attachments", TimeSpan.FromSeconds(20))
            .Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        declared.Should().NotBeNull("the owner declares it, so a client holding only the name can fetch it");
        declared!.Name.Should().Be("attachments");
        declared.BasePath.Should().EndWith(Path.Combine("attachments", "node1"));
    }

    [Fact(Timeout = 30000)]
    public async Task AnOwnerThatDeclaresNoSuchCollection_AnswersNull()
    {
        var owner = GetClient().Address;
        using var _ = AsViewer();

        var missing = await Mesh.GetOwnerCollectionConfig(owner, "nonexistent", TimeSpan.FromSeconds(20))
            .Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        missing.Should().BeNull("a collection the owner does not declare is not somebody else's files");
    }
}

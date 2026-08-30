using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Issues #2122 / #2123 — <c>ArgumentException: AssemblyName required for EmbeddedResource</c> on
/// every doc-tree image, in memex-cloud, on both pods, for days.
///
/// <para><b>The defect was not the registration.</b> Every embedded-resource collection is
/// registered by <c>AddEmbeddedResourceContentCollection</c>, which always writes
/// <c>Settings["AssemblyName"]</c> and <c>Settings["ResourcePrefix"]</c> — the two things the
/// provider factory needs to find its bytes. What lost them was the WIRE READ: the content route
/// asks the owning node hub for its collection configs with a <c>GetDataRequest</c>, the response
/// arrives as a <see cref="JsonElement"/>, and
/// <see cref="ContentFileResolver.ReadCollectionConfigs"/> rebuilt each config by hand from five
/// named properties — <c>Settings</c> not among them. The config that reached
/// <c>EmbeddedResourceStreamProviderFactory.Create</c> was therefore a TRUNCATED copy of a
/// perfectly good registration, and the guard three assemblies away threw about a field nobody had
/// omitted.</para>
///
/// <para>That is why this test asserts on BOTH halves and refuses to stop at the projection: it
/// reads the config off the wire exactly as the route does, and then actually serves the bytes of
/// two real shipped SVGs — the two paths named in the issues
/// (<c>Doc/GUI/DataBinding/icon.svg</c>, <c>Doc/AI/images/agenticai.svg</c>). A projection test
/// alone could pass while the asset stayed unservable for some other reason.</para>
/// </summary>
public class DocAssetWireProjectionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    // Partitioned persistence so the embedded "Doc" partition is actually served (a plain
    // non-partitioned in-memory adapter leaves the Doc namespace unreachable) — same harness as
    // DocContentEmbedRenderTest. Only AddDocumentation registers the collections; nothing is
    // mapped by hand, so what is under test is the shipped wiring.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddPartitionedInMemoryPersistence()
            .AddDocumentation()
            .AddGraph()
            .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// The end-to-end shape of the production failure: read the doc node's <c>content</c> collection
    /// config the way the content route reads it (over the wire, through
    /// <see cref="ContentFileResolver.ReadCollectionConfigs"/>), then open that collection and serve
    /// the file. Before the fix this threw
    /// <c>ArgumentException: AssemblyName required for EmbeddedResource</c> at the open step.
    /// </summary>
    /// <param name="nodePath">The doc node owning the asset.</param>
    /// <param name="file">The collection-relative file path.</param>
    [Theory(Timeout = 120000)]
    [InlineData("Doc/GUI/DataBinding", "icon.svg")]
    [InlineData("Doc/Architecture", "platform-overview.svg")]
    [InlineData("Doc/DataMesh", "data-product.svg")]
    public async Task DocAssetIsServable_AfterTheConfigCrossesTheWire(string nodePath, string file)
    {
        var nodeAddress = new Address(nodePath);
        var client = GetClient(c => c.AddContentCollections());

        // Wake the per-node hub so its collection registrations exist to be reported.
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress))
            .Should().Within(60.Seconds()).Emit();

        // The EXACT request ContentFileResolver.Resolve issues for a `{node}/{file…}` reference.
        var response = await client.Observe(
                new GetDataRequest(
                    new ContentCollectionReference([ContentCollectionsExtensions.DefaultCollectionName])),
                o => o.WithTarget(nodeAddress))
            .Should().Within(60.Seconds()).Emit();

        var configs = ContentFileResolver.ReadCollectionConfigs(response);
        configs.Should().NotBeNull("the node reports its content collection");

        var config = configs!.SingleOrDefault(
            c => c.Name == ContentCollectionsExtensions.DefaultCollectionName);
        config.Should().NotBeNull($"{nodePath} maps its own Content/ subfolder as 'content'");

        config!.SourceType.Should().Be("EmbeddedResource");
        config.IsStatic.Should().BeTrue(
            "the doc assets are published on the content route — this is the property whose loss "
            + "issue #587 fixed, and it must keep surviving the same projection");

        // 🚨 The regression assertion. Settings is what names the assembly and resource prefix;
        // without it the factory cannot find a single byte, and the failure surfaces three
        // assemblies away as a complaint about a field the registration DID supply.
        config.Settings.Should().NotBeNull(
            "Settings carries AssemblyName + ResourcePrefix for an EmbeddedResource collection — "
            + "dropping it on the wire is issues #2122/#2123");
        config.Settings!.GetValueOrDefault("AssemblyName").Should()
            .Be(typeof(DocumentationExtensions).Assembly.GetName().Name);
        config.Settings!.GetValueOrDefault("ResourcePrefix").Should()
            .StartWith("MeshWeaver.Documentation.Content");

        // …and now actually serve it, exactly as BlazorHostingExtensions.ResolveContentFile does:
        // register the config under its qualified name on THIS hub's content service, open the
        // collection, read the bytes.
        var qualified = config with { Name = $"{nodePath}/{config.Name}", Address = nodeAddress };
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        contentService.AddConfiguration(qualified);

        var collection = await contentService.GetCollection(qualified.Name)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(Ct);
        collection.Should().NotBeNull(
            "a collection whose provider factory threw resolves as null — which is how the route "
            + "answered 'Content read failed' for every doc image");

        collection!.GetContentType(file).Should().Be("image/svg+xml");

        var bytes = await collection.GetContentBytes(file)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(Ct);
        bytes.Should().NotBeNull($"{nodePath}/{file} ships as an embedded resource");
        bytes!.Length.Should().BePositive();
        Encoding.UTF8.GetString(bytes).Should().Contain("<svg",
            "the served bytes must be the real SVG, not an error page or an empty stream");
    }

    /// <summary>
    /// <c>Doc/AI</c> — the OTHER path named in issue #2122
    /// (<c>Doc/AI/images/agenticai.svg</c>) — separated out because only PART of that path's
    /// failure is this fix's to own, and pretending otherwise would overstate it.
    ///
    /// <para>What this fix does own, and what is asserted here: the config crosses the wire intact
    /// and the collection OPENS. Before it, <c>EmbeddedResourceStreamProviderFactory.Create</c>
    /// threw <c>ArgumentException: AssemblyName required for EmbeddedResource</c> and the route
    /// logged <c>Content read failed</c> at <c>fail:</c> level — the request never got as far as
    /// looking for a file.</para>
    ///
    /// <para>🚨 What it does NOT fix: <c>images/agenticai.svg</c> does not exist under
    /// <c>Content/AI/</c>. The asset ships at <c>Content/images/agenticai.svg</c> — the partition
    /// ROOT — while six <c>Doc/AI/*</c> pages declare <c>Thumbnail: "images/agenticai.svg"</c>,
    /// which resolves against their own folder node. That is a SECOND, independent defect (a
    /// thumbnail path pointing where the asset is not), it is not confined to this file —
    /// <c>images/DataMesh.svg</c> and <c>images/notifications.svg</c> are declared as thumbnails and
    /// ship nowhere either — and it is a doc-content question, not a registration one. After this
    /// fix that path answers a plain "file not found" instead of a 500, which is the honest answer
    /// for an asset that genuinely is not there.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task DocAiCollection_OpensWithACompleteConfig()
    {
        const string nodePath = "Doc/AI";
        var nodeAddress = new Address(nodePath);
        var client = GetClient(c => c.AddContentCollections());

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress))
            .Should().Within(60.Seconds()).Emit();

        var response = await client.Observe(
                new GetDataRequest(
                    new ContentCollectionReference([ContentCollectionsExtensions.DefaultCollectionName])),
                o => o.WithTarget(nodeAddress))
            .Should().Within(60.Seconds()).Emit();

        var config = ContentFileResolver.ReadCollectionConfigs(response)!
            .Single(c => c.Name == ContentCollectionsExtensions.DefaultCollectionName);

        config.Settings.Should().NotBeNull("this is the payload the wire read used to drop");
        config.Settings!.GetValueOrDefault("ResourcePrefix").Should()
            .Be("MeshWeaver.Documentation.Content.AI");

        var qualified = config with { Name = $"{nodePath}/{config.Name}", Address = nodeAddress };
        var contentService = client.ServiceProvider.GetRequiredService<IContentService>();
        contentService.AddConfiguration(qualified);

        var collection = await contentService.GetCollection(qualified.Name)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(Ct);
        collection.Should().NotBeNull(
            "the collection must now be constructible — the ArgumentException from the missing "
            + "AssemblyName is what made every read of it fail, whatever file was asked for");

        // The collection's OWN asset is servable, which is what proves the config is usable rather
        // than merely non-throwing.
        var icon = await collection!.GetContentBytes("icon.svg")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(Ct);
        icon.Should().NotBeNull("Content/AI/icon.svg ships in this collection");
    }

    /// <summary>
    /// The completeness guard that keeps the fix from rotting.
    ///
    /// <para>A hand-written projection is only ever correct for the properties its author needed.
    /// <c>ContentCollectionConfig</c> has already lost three this way — <c>IsStatic</c> (every
    /// published remote collection failed its mount check), <c>Address</c> (an inherited collection
    /// resolved against the ancestor's folder) and now <c>Settings</c> — each found in production
    /// rather than here. So this asserts EVERY property survives a real round trip through the hub
    /// serializer, and pins the property COUNT so that adding a fourth fails this test with a
    /// message saying what to do instead of shipping the same bug again.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void EveryConfigProperty_SurvivesTheWireProjection()
    {
        var original = new ContentCollectionConfig
        {
            Name = "content",
            SourceType = "EmbeddedResource",
            DisplayName = "Shipped assets",
            BasePath = "some/base",
            Order = 7,
            IsEditable = true,
            IsStatic = true,
            ExposeInChildren = true,
            Address = new Address("Doc/GUI/DataBinding"),
            Settings = new Dictionary<string, string>
            {
                ["AssemblyName"] = "MeshWeaver.Documentation",
                ["ResourcePrefix"] = "MeshWeaver.Documentation.Content.GUI.DataBinding",
            },
        };

        // Non-vacuity: if a future config type gains a property, this count fails first and says so.
        typeof(ContentCollectionConfig).GetProperties().Should().HaveCount(10,
            "every property of ContentCollectionConfig must be carried across "
            + "ContentFileResolver.ReadCollectionConfigs — add the new one there AND set it above, "
            + "then bump this count. Silently skipping it is issues #2122/#2123 all over again.");

        var projected = RoundTrip(original);

        projected.Name.Should().Be(original.Name);
        projected.SourceType.Should().Be(original.SourceType);
        projected.DisplayName.Should().Be(original.DisplayName);
        projected.BasePath.Should().Be(original.BasePath);
        projected.Order.Should().Be(original.Order);
        projected.IsEditable.Should().BeTrue();
        projected.IsStatic.Should().BeTrue();
        projected.ExposeInChildren.Should().BeTrue();
        projected.Address.Should().NotBeNull();
        projected.Address!.ToString().Should().Be(original.Address!.ToString());
        projected.Settings.Should().NotBeNull();
        projected.Settings!.Count.Should().Be(original.Settings!.Count);
        foreach (var (key, value) in original.Settings)
            projected.Settings.GetValueOrDefault(key).Should().Be(value,
                $"Settings['{key}'] must survive — for an EmbeddedResource collection these two "
                + "keys ARE the collection");
    }

    /// <summary>
    /// A config through the REAL hub serializer and back out of
    /// <see cref="ContentFileResolver.ReadCollectionConfigs"/> — the same two steps a cross-hub
    /// <c>GetDataResponse</c> takes.
    /// </summary>
    /// <param name="config">The config to round-trip.</param>
    /// <returns>The config as the reader reconstructs it.</returns>
    private ContentCollectionConfig RoundTrip(ContentCollectionConfig config)
    {
        var wire = JsonSerializer.SerializeToElement(
            (object)new[] { config }, Mesh.JsonSerializerOptions);
        wire.ValueKind.Should().Be(JsonValueKind.Array,
            "the reader enumerates an array — a different shape would make this test vacuous");

        var delivery = new MessageDelivery<GetDataResponse>(
            new Address("Doc/GUI/DataBinding"),
            Mesh.Address,
            new GetDataResponse(wire, 1),
            Mesh.JsonSerializerOptions);

        var configs = ContentFileResolver.ReadCollectionConfigs(delivery);
        configs.Should().NotBeNull().And.HaveCount(1);
        return configs!.Single();
    }
}

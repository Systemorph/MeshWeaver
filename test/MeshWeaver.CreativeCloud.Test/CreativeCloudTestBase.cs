using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.CreativeCloud.Domain;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.CreativeCloud.Test;

/// <summary>
/// Base class for CreativeCloud tests providing common setup and helper methods.
/// </summary>
public abstract class CreativeCloudTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc/>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureHub(c => c.AddData())
            .InstallAssemblies(typeof(CreativeCloudApplicationAttribute).Assembly.Location);
    }

    /// <summary>
    /// Gets all stories from the CreativeCloud application.
    /// </summary>
    protected async Task<IReadOnlyCollection<Story>> GetStoriesAsync()
    {
        var hub = Mesh;
        var response = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(Story))),
            o => o.WithTarget(CreativeCloudApplicationAttribute.Address),
            TestContext.Current.CancellationToken);

        return (response?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as Story ?? (x as JsonObject)?.Deserialize<Story>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<Story>()
            .ToList()
            ?? [];
    }

    /// <summary>
    /// Gets all persons from the CreativeCloud application.
    /// </summary>
    protected async Task<IReadOnlyCollection<Person>> GetPersonsAsync()
    {
        var hub = Mesh;
        var response = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(Person))),
            o => o.WithTarget(CreativeCloudApplicationAttribute.Address),
            TestContext.Current.CancellationToken);

        return (response?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as Person ?? (x as JsonObject)?.Deserialize<Person>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<Person>()
            .ToList()
            ?? [];
    }

    /// <summary>
    /// Gets all story arches from the CreativeCloud application.
    /// </summary>
    protected async Task<IReadOnlyCollection<StoryArch>> GetStoryArchesAsync()
    {
        var hub = Mesh;
        var response = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(StoryArch))),
            o => o.WithTarget(CreativeCloudApplicationAttribute.Address),
            TestContext.Current.CancellationToken);

        return (response?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as StoryArch ?? (x as JsonObject)?.Deserialize<StoryArch>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<StoryArch>()
            .ToList()
            ?? [];
    }

    /// <summary>
    /// Gets all content archetypes from the CreativeCloud application.
    /// </summary>
    protected async Task<IReadOnlyCollection<ContentArchetype>> GetContentArchetypesAsync()
    {
        var hub = Mesh;
        var response = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(ContentArchetype))),
            o => o.WithTarget(CreativeCloudApplicationAttribute.Address),
            TestContext.Current.CancellationToken);

        return (response?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as ContentArchetype ?? (x as JsonObject)?.Deserialize<ContentArchetype>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<ContentArchetype>()
            .ToList()
            ?? [];
    }

    /// <summary>
    /// Gets all content lenses from the CreativeCloud application.
    /// </summary>
    protected async Task<IReadOnlyCollection<ContentLens>> GetContentLensesAsync()
    {
        var hub = Mesh;
        var response = await hub.AwaitResponse(
            new GetDataRequest(new CollectionReference(nameof(ContentLens))),
            o => o.WithTarget(CreativeCloudApplicationAttribute.Address),
            TestContext.Current.CancellationToken);

        return (response?.Message?.Data as IEnumerable<object>)?
            .Select(x => x as ContentLens ?? (x as JsonObject)?.Deserialize<ContentLens>(hub.JsonSerializerOptions))
            .Where(x => x != null)
            .Cast<ContentLens>()
            .ToList()
            ?? [];
    }
}

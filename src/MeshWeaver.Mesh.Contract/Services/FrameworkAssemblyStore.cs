using System.Reactive.Linq;
using System.Reflection;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// <see cref="IAssemblyStore"/> that resolves framework-resident assemblies — types
/// shipped inside the running host (e.g. <c>MeshWeaver.Graph</c>, <c>MeshWeaver.AI</c>)
/// rather than dynamically compiled by Roslyn. Static NodeType providers register their
/// node with <c>LatestAssemblyCollection = "framework"</c> and
/// <c>LatestAssemblyPath = typeof(T).Assembly.GetName().Name</c>; activation calls
/// <see cref="TryGetAssemblyPath"/> with that name to find the matching already-loaded
/// assembly in the current AppDomain.
///
/// <para>This is what makes the "uniform Release model" work: every NodeType — static
/// or dynamic — looks up its bytes through an <see cref="IAssemblyStore"/>. Dynamic
/// types go through Blob/FileSystem; static types go here. The activation pipeline
/// has one codepath instead of two.</para>
/// </summary>
public sealed class FrameworkAssemblyStore : IAssemblyStore
{
    /// <summary>
    /// Sentinel collection name on <c>NodeTypeDefinition.LatestAssemblyCollection</c>
    /// that routes activation to this store. Set by static NodeType providers.
    /// </summary>
    public const string CollectionName = "framework";

    /// <summary>Shared singleton — stateless.</summary>
    public static readonly FrameworkAssemblyStore Instance = new();

    /// <summary>
    /// Looks up an already-loaded framework assembly by simple name (the
    /// <c>LatestAssemblyPath</c> value the static provider stamped). Returns the
    /// assembly's <c>Location</c> on hit, <c>null</c> on miss. The <paramref name="version"/>
    /// argument is ignored: framework assemblies are versioned by the host's deploy,
    /// not by per-NodeType compile generations.
    /// </summary>
    /// <param name="nodeTypePath">
    /// For framework lookups this is interpreted as the simple assembly name
    /// (e.g. <c>"MeshWeaver.Graph"</c>) — the value the static provider wrote into
    /// <c>NodeTypeDefinition.LatestAssemblyPath</c>.
    /// </param>
    /// <param name="version">Ignored — framework assemblies are not version-keyed.</param>
    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
    {
        if (string.IsNullOrEmpty(nodeTypePath))
            return Observable.Return<string?>(null);

        var match = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, nodeTypePath, StringComparison.Ordinal));
        if (match is null)
            return Observable.Return<string?>(null);

        // Some hosted scenarios load assemblies without a backing file (Single-file
        // publish, in-memory test fixtures). Empty Location is still a successful
        // "no need to LoadFrom" signal for the activation pipeline — the assembly is
        // already resolvable by name through the default ALC.
        return Observable.Return<string?>(string.IsNullOrEmpty(match.Location)
            ? string.Empty
            : match.Location);
    }

    /// <summary>
    /// Framework assemblies ship with the host — no upload required. No-op that
    /// returns the empty string so callers chaining <c>.SelectMany</c> still see a
    /// completion.
    /// </summary>
    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
        Observable.Return(string.Empty);

    /// <inheritdoc />
    public IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
        Observable.Return(new AssemblyStoreLocation(string.Empty, CollectionName, nodeTypePath));

    /// <summary>
    /// Convenience: produces the location pair a static NodeType provider should
    /// stamp into <c>NodeTypeDefinition.LatestAssembly{Collection,Path}</c> at
    /// registration. Caller passes a representative type from the framework
    /// assembly; the store records the assembly's simple name.
    /// </summary>
    public static AssemblyStoreLocation LocationFor(Assembly assembly) =>
        new(assembly.Location ?? string.Empty, CollectionName, assembly.GetName().Name ?? string.Empty);
}

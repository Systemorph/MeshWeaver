using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 A module batch is installed DEPENDENCIES FIRST — a module whose entry assembly references another
/// module of the batch is installed after it. Every host gets this through <see cref="MeshBuilder.InstallModules"/>.
///
/// <para><b>Why.</b> <see cref="MeshBuilder.InstallModules"/> loads, commits and MATERIALISES each module in
/// list order, and materialising runs the module's builder hook against a capture builder
/// (<c>ModuleContributions.Of</c>). A hook that names a type of another module resolves it through the
/// other module's CURRENT generation, which does not exist yet when the dependency sorts later. The
/// persisted boot list is name-sorted, and <c>MeshWeaver.Mail.Gmail</c> sorts before
/// <c>MeshWeaver.Mail.MicrosoftGraph</c>, so Gmail's hook threw <c>FileNotFoundException</c>, the capture
/// recorded "its builder hook could not be decomposed" as a blocker, and every later update of
/// Mail.MicrosoftGraph answered <c>RestartRequired</c> (memex.meshweaver.cloud, 2026-11-21). It was first
/// fixed in the Plugins portal composition root only; LocalMesh, the tester and every other host still
/// installed in name order, so the ordering lives here.</para>
///
/// <para><b>How.</b> Each entry DLL's assembly references are read from its METADATA (no load, no code
/// run) and the list is sorted topologically and STABLY: among modules that are ready the listed order
/// wins (name order at boot), so a batch with no edges between its members is returned unchanged. A DLL
/// that cannot be read keeps its place. The members of a reference cycle (and anything waiting on one)
/// are appended in their listed order, and the cycle is reported by name on stderr.</para>
/// </summary>
public static class ModuleInstallOrder
{
    /// <summary>The candidates, dependencies before their dependents.</summary>
    public static IReadOnlyList<ModuleInstallCandidate> DependenciesFirst(IReadOnlyList<ModuleInstallCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return DependenciesFirst(candidates, c => c.Location);
    }

    /// <summary>The <paramref name="items"/>, each located by <paramref name="location"/>, dependencies
    /// before their dependents.</summary>
    public static IReadOnlyList<T> DependenciesFirst<T>(IReadOnlyList<T> items, Func<T, string> location)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(location);
        var shapes = items.Select(item => Read(location(item))).ToArray();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < shapes.Length; i++)
            if (shapes[i].Name is { } name)
                indexByName.TryAdd(name, i);

        // Edges inside the batch only: i depends on j when i references j's entry assembly.
        var dependsOn = shapes
            .Select((shape, i) => shape.References
                .Select(r => indexByName.TryGetValue(r, out var j) && j != i ? j : -1)
                .Where(j => j >= 0)
                .ToHashSet())
            .ToArray();

        var placed = new bool[items.Count];
        var order = new List<T>(items.Count);
        while (true)
        {
            // The first ready item in listed order; restarting from the top keeps an earlier item that
            // this placement released ahead of later ones.
            var next = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (placed[i] || dependsOn[i].Any(j => !placed[j]))
                    continue;
                next = i;
                break;
            }
            if (next < 0)
                break;
            placed[next] = true;
            order.Add(items[next]);
        }

        var stuck = Enumerable.Range(0, items.Count).Where(i => !placed[i]).ToArray();
        if (stuck.Length > 0)
        {
            Console.Error.WriteLine(
                "[MeshWeaver.Mesh.ModuleInstallOrder] modules reference each other in a cycle (or wait on one); "
                + "installing them in listed order: "
                + string.Join(", ", stuck.Select(i => shapes[i].Name ?? Path.GetFileNameWithoutExtension(location(items[i])))));
            order.AddRange(stuck.Select(i => items[i]));
        }
        return order;
    }

    private static (string? Name, ImmutableArray<string> References) Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return (null, []);
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return (null, []);
            var name = reader.GetString(reader.GetAssemblyDefinition().Name);
            var references = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .ToImmutableArray();
            return (name, references);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            // Unreadable here: the loader will say why when it tries; the order keeps its place.
            return (null, []);
        }
    }
}

using MeshWeaver.Compiler;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>The release surface includes the image's seeded module entries, not just /app.</summary>
internal static class PublishedHostSurface
{
    internal static ModulePlatformSurface Read(string appDirectory, IEnumerable<string> platformFiles)
    {
        var (shipped, problem) = PlatformShippedAssemblies.Read(appDirectory);
        if (shipped is null)
            throw new InvalidDataException(problem);

        // Reuse the loader's shipped-assembly witness. A private DLL riding beside an entry is
        // NOT another module's probe location; only <modules>/<name>/<name>.dll belongs here.
        // The app/framework closure wins duplicate names, as it does in the runtime binder.
        var seeded = shipped.Values
            .Where(assembly => assembly.How == PlatformShipping.SeededModule)
            .OrderBy(assembly => assembly.Name, StringComparer.Ordinal)
            .ToArray();
        var surface = ModulePlatformSurface.OfFiles(
            platformFiles.Concat(seeded.Select(assembly => assembly.Evidence)));
        foreach (var assembly in seeded)
            if (surface.TypesOf(assembly.Name) is null)
                throw new InvalidDataException(
                    $"The image ships '{assembly.Evidence}', but its type surface is unreadable.");
        return surface;
    }
}

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
        // The witness classifies ownership (root before seed); resolve the entry paths with
        // MeshBuilder.ResolveModulePath's image-before-app order, including double-shipped names.
        var seeded = shipped.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new PlatformShippedAssembly(name, PlatformShipping.SeededModule,
                Path.Combine(appDirectory, PlatformShippedAssemblies.SeededModulesFolder, name, name + ".dll")))
            .Where(assembly => File.Exists(assembly.Evidence))
            .ToArray();
        var surface = ModulePlatformSurface.OfFiles(
            seeded.Select(assembly => assembly.Evidence).Concat(platformFiles));
        foreach (var assembly in seeded)
            if (surface.TypesOf(assembly.Name) is null)
                throw new InvalidDataException(
                    $"The image ships '{assembly.Evidence}', but its type surface is unreadable.");
        return surface;
    }
}

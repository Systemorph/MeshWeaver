using MeshWeaver.Compiler;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>The release surface includes the image's seeded module entries, not just /app.</summary>
internal static class PublishedHostSurface
{
    internal static ModulePlatformSurface Read(string appDirectory, IEnumerable<string> platformFiles)
        => ModulePlatformSurface.OfFiles(Files(appDirectory, platformFiles));

    /// <summary>The surface's FILES in binding precedence (the image's seeded module entries first,
    /// then <paramref name="platformFiles"/>) — so a caller can extend the surface with more files
    /// behind the platform's, as <c>platform-link</c> does with a published set's sibling modules.</summary>
    internal static IReadOnlyList<string> Files(string appDirectory, IEnumerable<string> platformFiles)
    {
        var files = Files(appDirectory, platformFiles, out var seeded);
        var surface = ModulePlatformSurface.OfFiles(files);
        foreach (var assembly in seeded)
            if (surface.TypesOf(assembly.Name) is null)
                throw new InvalidDataException(
                    $"The image ships '{assembly.Evidence}', but its type surface is unreadable.");
        return files;
    }

    private static IReadOnlyList<string> Files(
        string appDirectory, IEnumerable<string> platformFiles, out PlatformShippedAssembly[] seeded)
    {
        var (shipped, problem) = PlatformShippedAssemblies.Read(appDirectory);
        if (shipped is null)
            throw new InvalidDataException(problem);

        // Reuse the loader's shipped-assembly witness. A private DLL riding beside an entry is
        // NOT another module's probe location; only <modules>/<name>/<name>.dll belongs here.
        // The witness classifies ownership (root before seed); resolve the entry paths with
        // MeshBuilder.ResolveModulePath's image-before-app order, including double-shipped names.
        seeded = shipped.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new PlatformShippedAssembly(name, PlatformShipping.SeededModule,
                Path.Combine(appDirectory, PlatformShippedAssemblies.SeededModulesFolder, name, name + ".dll")))
            .Where(assembly => File.Exists(assembly.Evidence))
            .ToArray();
        return seeded.Select(assembly => assembly.Evidence).Concat(platformFiles).ToList();
    }
}

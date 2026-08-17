using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.Compiler;

/// <summary>
/// The metadata-reference set a dynamic NodeType compile is built against — the process-wide TPA
/// baseline plus this mesh's installed-module composition. Which assemblies Roslyn can see is
/// part of the compile's input, so the construction lives inside the toolchain identity boundary
/// (#1707); MeshWeaver.Graph resolves the module list from the mesh's service tree and threads it
/// in.
/// </summary>
public static class CompileReferences
{
    /// <summary>
    /// The process-wide default reference list — TPA assemblies plus a few well-known additions,
    /// built once at type load (eager static init: a plain field read on every compile, no Lazy
    /// dispatch, no synchronization; an immutable constant lookup in the NoStaticState.md sense).
    /// </summary>
    public static IReadOnlyList<MetadataReference> Default { get; } = GetDefaultReferences();

    /// <summary>
    /// Builds the process-wide MetadataReference list — TPA assemblies plus a few
    /// well-known additions. Uses <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>
    /// (mmap, lazy read) — Roslyn typically reads only a small fraction of each
    /// assembly's metadata, so the upfront cost is tiny. An earlier attempt at
    /// <see cref="MetadataReference.CreateFromStream(System.IO.Stream, MetadataReferenceProperties, DocumentationProvider, string?)"/>
    /// to avoid finalizer pressure ended up reading the whole DLL into managed
    /// memory eagerly — net 10%+ slower in the autocomplete-test CPU profile,
    /// since most of those bytes were never touched. The file-handle finalizer
    /// pressure those references add is also tiny in practice (the static
    /// property holds them for the process lifetime; finalizers only run at shutdown).
    /// </summary>
    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }
        }

        // Three well-known additions in case TPA didn't include them. Dedup
        // against TPA-derived set by Display path so we don't double-load.
        var seen = new HashSet<string>(
            references.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        })
        {
            if (!string.IsNullOrEmpty(assembly.Location)
                && File.Exists(assembly.Location)
                && seen.Add(assembly.Location))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch
                {
                    // Skip if it can't be loaded
                }
            }
        }

        return references;
    }

    /// <summary>
    /// Composes <see cref="Default"/> with THIS mesh's installed module assemblies
    /// (<c>Modules:Assemblies</c> → <see cref="InstalledModuleAssembly"/>, design #1644). A module
    /// published into <c>modules/&lt;name&gt;/</c> is not in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>,
    /// so in-mesh consumers (e.g. a scope class using a map control) would silently lose it
    /// without this. Deduped by path — a module still riding the app closure appears in both
    /// sets. Returns <see cref="Default"/> itself for a zero-module mesh.
    /// </summary>
    public static IReadOnlyList<MetadataReference> ComposeWithModules(
        IReadOnlyList<InstalledModuleAssembly> modules)
    {
        if (modules.Count == 0)
            return Default;
        var seen = new HashSet<string>(
            Default.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        var composed = new List<MetadataReference>(Default);
        foreach (var module in modules)
        {
            var location = module.Assembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location) && seen.Add(location))
                composed.Add(MetadataReference.CreateFromFile(location));
        }
        return composed;
    }
}

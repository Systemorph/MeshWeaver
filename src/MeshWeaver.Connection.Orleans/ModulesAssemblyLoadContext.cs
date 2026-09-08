using System.Reflection;
using System.Runtime.Loader;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for loading mesh module assemblies on
/// demand. Resolves a dependency through the PLATFORM first — whatever the default context can
/// supply for the name, loaded yet or not — then from the current working directory, then from
/// a configured base path.
///
/// <para>🚨 <b>Why "can supply" and not "has already loaded" (#3662).</b> The first step used to
/// scan <see cref="AssemblyLoadContext.Default"/>'s loaded assemblies for the name. A platform
/// assembly the process had not touched yet matched nothing there, so a bundle copy of it in the
/// module directory won this context — and when the platform later loaded its own copy, the
/// process held two assemblies of one identity: every type crossing the boundary failed as an
/// <c>InvalidCastException</c> between two types of the same full name. The portal image carries
/// 214 platform assemblies and the tester image 88; a bundle may legitimately carry one the tester
/// lacks (<c>MeshWeaver.ContentCollections.Indexing</c>, <c>MeshWeaver.Blazor</c>), and on the
/// portal that copy must lose to the platform's. Asking the default context to RESOLVE the name
/// is the answer that is right on both: the platform serves what it has, and the module's own
/// copy is used only for what the platform has not got.</para>
/// </summary>
/// <param name="basePath">Fallback directory probed for an assembly DLL when the platform cannot
/// supply it and it is not in the current working directory.</param>
public class ModulesAssemblyLoadContext(string basePath) : AssemblyLoadContext(true){

    /// <summary>
    /// Resolves and loads the requested assembly: the platform's copy when the default context
    /// can resolve the name (already loaded or not), else a DLL in the current working directory,
    /// else one under the configured <c>basePath</c>.
    /// </summary>
    /// <param name="assemblyName">The name of the assembly to resolve.</param>
    /// <returns>The loaded assembly, or <c>null</c> if it could not be located.</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The platform first — RESOLVED, not merely "already loaded". A name the default context
        // can bind (the trusted platform assemblies, the application's own closure) is served from
        // there whether or not anything has loaded it yet, so a same-named copy beside the module
        // can never split the identity. The three exceptions are the default context's ways of
        // saying "I have no such assembly"; anything else is a real fault and propagates.
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not the platform's: fall through to the module's own closure.
        }

        var assemblyPath = Path.Combine(Directory.GetCurrentDirectory(), $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);

        assemblyPath = Path.Combine(basePath, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);

        return null;
    }
}

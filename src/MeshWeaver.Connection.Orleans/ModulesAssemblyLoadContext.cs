using System.Reflection;
using System.Runtime.Loader;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for loading mesh module assemblies on
/// demand. Resolves dependencies first from the default context, then from the current
/// working directory, then from a configured base path.
/// </summary>
/// <param name="basePath">Fallback directory probed for an assembly DLL when it cannot be
/// found in the default context or the current working directory.</param>
public class ModulesAssemblyLoadContext(string basePath) : AssemblyLoadContext(true){

    /// <summary>
    /// Resolves and loads the requested assembly, preferring an already-loaded copy in the
    /// default context, then a DLL in the current working directory, then one under
    /// the configured <c>basePath</c>.
    /// </summary>
    /// <param name="assemblyName">The name of the assembly to resolve.</param>
    /// <returns>The loaded assembly, or <c>null</c> if it could not be located.</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Check if the assembly is already loaded
        var loadedAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);

        if (loadedAssembly != null)
            return loadedAssembly;


        var assemblyPath = Path.Combine(Directory.GetCurrentDirectory(), $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);

        assemblyPath = Path.Combine(basePath, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);


        return null;
    }
}

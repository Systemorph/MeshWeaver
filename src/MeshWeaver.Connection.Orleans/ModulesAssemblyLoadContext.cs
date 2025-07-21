using System.Reflection;
using System.Runtime.Loader;

namespace MeshWeaver.Connection.Orleans;

public class ModulesAssemblyLoadContext(string basePath) : AssemblyLoadContext(true){

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

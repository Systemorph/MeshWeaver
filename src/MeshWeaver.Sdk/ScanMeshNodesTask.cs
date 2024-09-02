using System.Reflection;
using MeshWeaver.Mesh.Contract;
using Microsoft.Build.Framework;

namespace MeshWeaver.Sdk;

public class ScanMeshNodesTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Assemblies { get; set; }

    [Required]
    public string OutputDirectory { get; set; }

    public override bool Execute()
    {
        foreach (var assemblyItem in Assemblies)
        {
            var assemblyPath = assemblyItem.ItemSpec;
            var assembly = Assembly.LoadFrom(assemblyPath);

            var hasMeshNodeAttribute = assembly.GetCustomAttributes()
                .Any(attr => attr.GetType().IsSubclassOf(typeof(MeshNodeAttribute)));

            if (hasMeshNodeAttribute)
            {
                var outputFilePath = Path.Combine(OutputDirectory, $"{Path.GetFileName(assemblyPath)}.txt");
                File.WriteAllText(outputFilePath, assemblyPath);
            }
        }

        return true;
    }
}

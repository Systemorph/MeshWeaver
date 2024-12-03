using MeshWeaver.Hosting.Notebook;
using MeshWeaver.Layout;
using Microsoft.DotNet.Interactive;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.DotNet.Interactive.CSharp;

public static class NotebookMeshClientExtensions
{
    public static NotebookMeshClient ConfigureMesh(
        this Kernel kernel, 
        string url, 
        object address = null)
    {
        var builder = new NotebookMeshClient(kernel, address ?? new NotebookAddress(), url)
            .ConfigureHub(config => 
                config
                    .AddLayout(layout => layout));
        return builder;
    }

    internal static IEnumerable<Kernel> SubKernelsAndSelf(this Kernel kernel)
    {
        yield return kernel;

        if (kernel is CompositeKernel compositeKernel)
        {
            foreach (var subKernel in compositeKernel.ChildKernels)
            {
                yield return subKernel;
            }
        }
    }

    internal static async Task UseMeshWeaverAsync(this CSharpKernel kernel, IMessageHub hub, CancellationToken ct)
    {
        await kernel.SubmitCodeAsync(
            @$"using {typeof(MeshExtensions).Namespace};
using {typeof(MessageHubExtensions).Namespace};");
    }



}

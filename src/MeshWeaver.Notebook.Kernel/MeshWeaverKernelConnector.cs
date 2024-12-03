using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;

namespace MeshWeaver.Notebook.Client;

public class MeshWeaverKernelConnector(
    IServiceProvider serviceProvider,
    MeshConnection mesh,
    string kernelSpecName,
    string initScript)
{
    private readonly string kernelSpecName = kernelSpecName ?? throw new ArgumentNullException(nameof(kernelSpecName));

    public async Task<Microsoft.DotNet.Interactive.Kernel> CreateKernelAsync(string kernelName)
    {
        var kernelConnection = await CreateKernelConnectionAsync(kernelSpecName);

        var kernel = await MeshWeaverKernel.CreateAsync(kernelName, kernelConnection.Hub, kernelConnection.Address);

        if (!string.IsNullOrEmpty(initScript))
        {
            await kernel.RunOnKernelAsync(initScript);
        }


        kernel.RegisterForDisposal(kernelConnection);
        return kernel;
    }

    private async Task<MeshWeaverKernelConnection> CreateKernelConnectionAsync(string s)
    {
        throw new NotImplementedException();
    }
}


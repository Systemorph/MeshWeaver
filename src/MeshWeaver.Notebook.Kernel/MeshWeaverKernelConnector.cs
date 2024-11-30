using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;

namespace MeshWeaver.Notebook.Client;

public class MeshWeaverKernelConnector
{
    private readonly IServiceProvider serviceProvider;
    private readonly MeshWeaverConnection meshWeaverConnection;
    private readonly string kernelSpecName;
    private readonly string initScript;

    public MeshWeaverKernelConnector(IServiceProvider serviceProvider, MeshWeaverConnection meshWeaver, string kernelSpecName, string initScript)
    {
        this.serviceProvider = serviceProvider;
        meshWeaverConnection = meshWeaver ?? throw new ArgumentNullException(nameof(meshWeaver));
        this.kernelSpecName = kernelSpecName ?? throw new ArgumentNullException(nameof(kernelSpecName));
        this.initScript = initScript;
    }

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


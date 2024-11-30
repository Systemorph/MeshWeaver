using MeshWeaver.Messaging;

namespace MeshWeaver.Notebook.Kernel;
internal class MeshWeaverKernelConnector
{
    private readonly IServiceProvider serviceProvider;
    private readonly IMeshWeaverConnection meshWeaverConnection;
    private readonly string kernelSpecName;
    private readonly string initScript;

    public MeshWeaverKernelConnector(IServiceProvider serviceProvider, IMeshWeaverConnection meshWeaver, string kernelSpecName, string initScript)
    {
        this.serviceProvider = serviceProvider;
        meshWeaverConnection = meshWeaver ?? throw new ArgumentNullException(nameof(meshWeaver));
        this.kernelSpecName = kernelSpecName ?? throw new ArgumentNullException(nameof(kernelSpecName));
        this.initScript = initScript;
    }

    public async Task<Microsoft.DotNet.Interactive.Kernel> CreateKernelAsync(string kernelName)
    {
        var kernelConnection = await meshWeaverConnection.CreateKernelConnectionAsync(kernelSpecName);

        await kernelConnection.StartAsync(serviceProvider);
        var kernel = await MeshWeaverKernel.CreateAsync(kernelName, kernelConnection.Hub, kernelConnection.Address);

        if (!string.IsNullOrEmpty(initScript))
        {
            await kernel.RunOnKernelAsync(initScript);
        }


        kernel.RegisterForDisposal(kernelConnection);
        return kernel;
    }
}

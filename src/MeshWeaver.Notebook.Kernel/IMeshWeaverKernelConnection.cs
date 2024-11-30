using MeshWeaver.Messaging;
using MeshWeaver.Notebooks;

namespace MeshWeaver.Notebook.Kernel;

public record MeshWeaverKernelConnection(string Url, object Address) : IDisposable
{
    public IMessageHub Hub { get; private set; }
    public void Dispose()
    {
        Hub.Dispose();
    }

    public Task StartAsync(IServiceProvider serviceProvider)
    {
        var address = new NotebookClientAddress(Url);
        Hub = serviceProvider.CreateMessageHub(address, conf => conf);
        return Task.CompletedTask;
    }


}

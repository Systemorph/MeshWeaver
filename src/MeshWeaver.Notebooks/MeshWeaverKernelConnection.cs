using MeshWeaver.Messaging;

namespace MeshWeaver.Notebooks;

public record MeshWeaverKernelConnection : IDisposable
{

    public IMessageHub Hub { get; init; }
    public string Url { get; init; }
    public object Address { get; init; }

    public void Dispose()
    {
        Hub.Dispose();
    }

}

namespace MeshWeaver.Notebook.Kernel;

public interface IMeshWeaverConnection
{

    Task<MeshWeaverKernelConnection> CreateKernelConnectionAsync(string kernelSpecName);
}

public class MeshWeaverConnection : IMeshWeaverConnection
{
    public Task<MeshWeaverKernelConnection> CreateKernelConnectionAsync(string kernelSpecName)
    {
        throw new NotImplementedException();
    }
}

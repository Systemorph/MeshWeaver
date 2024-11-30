namespace MeshWeaver.Notebooks.Hub;

public interface IKernelHost
{
    Task SubmitCode(string code);
}


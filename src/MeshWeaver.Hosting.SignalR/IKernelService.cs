using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.SignalR
{
    public interface IKernelService
    {

        Task SubmitCommandAsync(KernelAddress kernelAddress, string commandEnvelope, string layoutAreaUrl);
        Task SubmitEventAsync(KernelAddress kernelAddress, string commandEnvelope);
        void DisposeKernel(KernelAddress kernelAddress);
    }
}

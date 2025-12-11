using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.SignalR
{
    public interface IKernelService
    {

        Task SubmitCommandAsync(Address kernelAddress, string commandEnvelope, string? layoutAreaUrl);
        Task SubmitEventAsync(Address kernelAddress, string commandEnvelope);
        void DisposeKernel(Address kernelAddress);
    }
}

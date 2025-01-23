using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.SignalR
{
    public class KernelService(IMessageHub hub, IMemoryCache memoryCache) : IKernelService
    {
        private async Task<KernelClient> GetKernelClientAsync(KernelAddress kernelAddress)
        {
            var client = await memoryCache.GetOrCreateAsync(
                kernelAddress, _ => Task.FromResult(new KernelClient(hub, kernelAddress)),
                new()
                {
                    SlidingExpiration = TimeSpan.FromMinutes(15),
                    PostEvictionCallbacks =
                    {
                        new PostEvictionCallbackRegistration { EvictionCallback = DisposeKernel }
                    }
                });
            return client;
        }

        private void DisposeKernel(object key, object value, EvictionReason reason, object state)
        {
            ((KernelClient)value).Dispose();
        }

        public Task SubmitCommandAsync(KernelAddress kernelAddress, string kernelCommandEnvelope, string layoutAreaUrl) => 
            PostToKernel(new KernelCommandEnvelope(kernelCommandEnvelope){IFrameUrl = layoutAreaUrl}, kernelAddress);

        public Task SubmitEventAsync(KernelAddress kernelAddress, string commandEnvelope) => 
            PostToKernel(new KernelEventEnvelope(commandEnvelope), kernelAddress);

        public void DisposeKernel(KernelAddress kernelAddress)
        {
            if (memoryCache.TryGetValue(kernelAddress, out var val) && val is KernelClient client)
            {
                memoryCache.Remove(kernelAddress);
                client.Dispose();
            }
        }

        private async Task PostToKernel(object message, KernelAddress kernelAddress)
        {
            var client = await GetKernelClientAsync(kernelAddress);
            client.PostToKernel(message);
        }

    }

    public class KernelClient : IDisposable
    {
        private readonly IMessageHub hub;
        private readonly Timer timer;
        public KernelClient(IMessageHub hub, KernelAddress kernelAddress)
        {
            this.hub = hub;
            timer = new(SendHeartBeat, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            KernelAddress = kernelAddress;
        }

        public void SendHeartBeat(object _)
        {
            hub.Post(new HeartbeatEvent(), o => o.WithTarget(KernelAddress));
        }

        public KernelAddress KernelAddress { get; }

        public void Dispose()
        {
            timer.Dispose();
        }


        public void PostToKernel(object message)
        {
            hub.Post(
                message,
                o => o.WithTarget(KernelAddress)
            );
        }
    }
}

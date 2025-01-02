using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.SignalR
{
    public class KernelService(IMessageHub hub, IMemoryCache memoryCache) : IKernelService
    {


        public Task<string> GetKernelIdAsync(string clientId)
            => memoryCache.GetOrCreateAsync(clientId, _ => Task.FromResult(Guid.NewGuid().AsString()), new()
            {
                SlidingExpiration = TimeSpan.FromMinutes(5),
                PostEvictionCallbacks = { new PostEvictionCallbackRegistration
                {
                    EvictionCallback = DisposeKernel
                } }
            });

        private void DisposeKernel(object key, object value, EvictionReason reason, object state)
        {

        }

        public async Task SubmitCommandAsync(string clientId, string kernelCommandEnvelope)
        {
            PostToKernel(new KernelCommandEnvelope(kernelCommandEnvelope), await GetKernelIdAsync(clientId));
        }

        private void PostToKernel(object message, string kernelId)
        {
            hub.Post(message, o => o.WithTarget(new KernelAddress() { Id = kernelId }));
        }

    }
}

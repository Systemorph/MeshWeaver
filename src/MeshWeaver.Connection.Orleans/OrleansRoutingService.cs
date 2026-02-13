using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans;

public class OrleansRoutingService : IRoutingService, IDisposable
{
    private readonly IGrainFactory grainFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<OrleansRoutingService> logger;
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();
    private readonly Channel<(IMessageDelivery Delivery, Address Address)> outboundChannel;
    private readonly CancellationTokenSource cts = new();
    private readonly Task consumerTask;

    public OrleansRoutingService(
        IGrainFactory grainFactory,
        IServiceProvider serviceProvider,
        ILogger<OrleansRoutingService> logger)
    {
        this.grainFactory = grainFactory;
        this.serviceProvider = serviceProvider;
        this.logger = logger;

        outboundChannel = Channel.CreateUnbounded<(IMessageDelivery, Address)>(
            new UnboundedChannelOptions { SingleReader = true });

        consumerTask = Task.Run(() => ConsumeOutboundAsync(cts.Token));
    }

    public Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken = default)
    {
        var target = delivery.Target;
        if (target == null)
            return Task.FromResult(delivery);

        var address = GetHostAddress(target);

        // 1. Check registered local streams (portals, in-process clients)
        if (streams.TryGetValue(address, out var callback))
            return callback(delivery, cancellationToken);

        // 2. Enqueue for background delivery via RoutingGrain
        outboundChannel.Writer.TryWrite((delivery, address));
        return Task.FromResult(delivery.Forwarded(address));
    }

    private async Task ConsumeOutboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (delivery, address) in outboundChannel.Reader.ReadAllAsync(ct))
            {
                _ = DeliverViaGrainAsync(delivery, address, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task DeliverViaGrainAsync(IMessageDelivery delivery, Address address, CancellationToken ct)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromMilliseconds(200);
        var maxDelay = TimeSpan.FromSeconds(30);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var grain = grainFactory.GetGrain<IRoutingGrain>("default");
                await grain.RouteMessage(delivery);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientFailure(ex))
            {
                logger.LogDebug(ex, "Transient failure delivering to {Address}, attempt {Attempt}/{MaxRetries}",
                    address, attempt + 1, maxRetries);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deliver to {Address} after {Attempts} attempts",
                    address, attempt + 1);
                return;
            }
        }
    }

    private static bool IsTransientFailure(Exception ex)
    {
        return ex is SocketException
            or HttpRequestException
            or TimeoutException
            or global::Orleans.Runtime.OrleansMessageRejectionException
            || (ex.InnerException != null && IsTransientFailure(ex.InnerException));
    }

    public async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;

        // Also subscribe to Orleans memory stream so cross-process messages arrive
        var stream = GetStreamProvider(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscription = await stream.SubscribeAsync((v, _) =>
            callback.Invoke(v, CancellationToken.None));

        return new AnonymousAsyncDisposable(async () =>
        {
            streams.TryRemove(address, out _);
            await subscription.UnsubscribeAsync();
        });
    }

    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    internal static Address GetHostAddress(Address address)
    {
        if (address.Host != null)
        {
            var host = GetHostAddress(address.Host);
            if (host.Type == AddressExtensions.MeshType)
                return address with { Host = null };
            return host;
        }
        return address;
    }

    public void Dispose()
    {
        cts.Cancel();
        outboundChannel.Writer.TryComplete();

        // Wait briefly for the consumer to drain
        consumerTask.Wait(TimeSpan.FromSeconds(5));
        cts.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection(IServiceProvider serviceProvider, Address address) : IDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    public Address Host { get; } = address;
    private readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>();

    private readonly ConcurrentDictionary<Address, IMessageHub> messageHubs = new(AddressComparer.Instance);

    private readonly Subject<IMessageHub> _hubAdded = new();
    /// <summary>
    /// Emits each <see cref="IMessageHub"/> as it's added to this collection.
    /// Routes that need a hub that may register slightly later (cross-thread
    /// sync sub-hub creation race) can subscribe to this and re-attempt
    /// delivery when the matching hub appears. Hot subject — late subscribers
    /// miss prior emissions; pair with a synchronous <see cref="GetHub"/>
    /// check first.
    /// </summary>
    public IObservable<IMessageHub> HubAdded => _hubAdded.AsObservable();

    public IMessageHub? GetHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
    {
        if (messageHubs.TryGetValue(address, out var hub))
            return hub;

        // 🚨 Never-create lookups are PURE READS and must not touch any lock:
        // RouteStreamMessage probes this per stream message per parent-chain
        // level (HostedHubCreation.Never). The previous shape funneled every
        // MISS into the global creation lock — and hub CONSTRUCTION also ran
        // inside that lock — so any creation burst (post-deploy enrichment,
        // prerender sync hubs) convoyed every routed stream message behind it.
        // dotnet-stack proof, twice on 2026-06-12 atioz: the hottest frame was
        // Monitor.Enter_Slowpath ← GetHub ← RouteStreamMessage ← DrainOne,
        // once pegging the drain thread at 99.9% CPU (10k-hub storm) and once
        // burning an Orleans grain turn for minutes (the AgenticPension space
        // "wedge": queue backing up behind a multi-minute drain turn).
        if (create != HostedHubCreation.Always)
            return null;

        if (IsDisposing)
        {
            logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", address, Host);
            return null;
        }

        // Per-address single-flight; CONSTRUCTION RUNS OUTSIDE ANY GLOBAL LOCK.
        // Concurrent creators of the SAME address share one Lazy (second caller
        // blocks only on that address); creators of different addresses never
        // contend. The factory re-checks messageHubs so a creator racing the
        // post-construction cleanup below cannot build a duplicate hub.
        var lazy = creations.GetOrAdd(address, a => new Lazy<IMessageHub?>(() =>
        {
            if (messageHubs.TryGetValue(a, out var existing))
                return existing;
            if (IsDisposing)
            {
                logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", a, Host);
                return null;
            }
            var newHub = CreateHub(a, config);
            if (newHub != null)
            {
                messageHubs[a] = newHub;
                try { _hubAdded.OnNext(newHub); } catch { /* never throw on notification */ }
            }
            return newHub;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        var created = lazy.Value;
        // The Lazy only guards single-flight DURING construction — messageHubs is
        // the steady-state map (same as before). Dropping the entry afterwards
        // also restores the old retry semantics when creation failed/was refused.
        creations.TryRemove(address, out _);
        return created;
    }

    /// <summary>
    /// Per-address construction single-flight (see <see cref="GetHub"/>). Entries
    /// live only for the duration of one construction; <see cref="messageHubs"/>
    /// remains the steady-state registry.
    /// </summary>
    private readonly ConcurrentDictionary<Address, Lazy<IMessageHub?>> creations = new(AddressComparer.Instance);

    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
        try { _hubAdded.OnNext(hub); } catch { /* never throw on notification */ }
    }

    private IMessageHub? CreateHub(Address address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        if (IsDisposing)
        {
            logger.LogWarning("Preventing hub creation for address {Address} in host {Host} - collection is disposing", address, Host);
            return null;
        }

        try
        {
            logger.LogDebug("Creating new hosted hub for address {Address} in host {Host} ", address, Host);
            var hub = serviceProvider.CreateMessageHub(address, config);
            return hub;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create hosted hub for address {Address}", address);
            return null;
        }
    }

    private readonly object locker = new();
    private bool IsDisposing => disposalStarted;
    private volatile bool disposalStarted;

    // Reactive completion source of truth — completed exactly once (CAS-guarded) when every
    // hosted hub has finished disposing (or the 10 s cap elapses). ReplaySubject(1) so a late
    // subscriber (the owning hub's ShutDown phase) still observes the terminal notification.
    private readonly ReplaySubject<Unit> disposalCompleted = new(1);
    private int disposalSignalled;
    private IDisposable? disposalSubscription;

    /// <summary>
    /// Observable completion of the collection's disposal — fires <see cref="Unit"/> + completes
    /// once ALL hosted hubs have finished disposing (or the 10 s cap elapses). Native reactive
    /// surface (NOT bridged from a Task); the owning <see cref="MessageHub"/> subscribes to it to
    /// advance its own ShutDown phase, never awaiting a Task on the action block.
    /// </summary>
    public IObservable<Unit> DisposalCompleted => disposalCompleted.AsObservable();

    public void Dispose()
    {
        lock (locker)
        {
            if (disposalStarted) return;
            disposalStarted = true;
        }
        DisposeHubsReactive();
    }

    /// <summary>
    /// Disposes each hosted hub SYNCHRONOUSLY (kicking off its own reactive disposal), then
    /// OBSERVES their collective completion — no <c>async</c>/<c>await</c>, no
    /// <c>Task.WhenAll</c>. Per-child <c>Catch</c> keeps one wedged/faulted child from stalling
    /// the join (CombineLatest needs an emission from every input); the 10 s <c>Timeout</c> caps
    /// the whole wait so the owning hub's ShutDown phase is never blocked.
    /// </summary>
    private void DisposeHubsReactive()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var hubs = messageHubs.Values.ToArray();
        logger.LogDebug("Starting disposal of {count} hosted hubs: [{hubAddresses}]",
            hubs.Length, string.Join(", ", hubs.Select(h => h.Address.ToString())));

        var childCompletions = hubs.Select(h =>
        {
            var address = h.Address;
            try
            {
                h.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during disposal of hub {address}", address);
            }
            return h.DisposalCompleted
                .Take(1)
                .Catch<Unit, Exception>(ex =>
                {
                    logger.LogError(ex, "Hub {address} disposal faulted", address);
                    return Observable.Return(Unit.Default);
                });
        }).ToArray();

        IObservable<Unit> all = childCompletions.Length == 0
            ? Observable.Return(Unit.Default)
            : Observable.CombineLatest(childCompletions).Select(_ => Unit.Default).Take(1);

        disposalSubscription = all
            .Timeout(TimeSpan.FromSeconds(5))
            .Subscribe(
                _ =>
                {
                    logger.LogDebug("All {count} hosted hubs disposed successfully in {elapsed}ms",
                        hubs.Length, totalStopwatch.ElapsedMilliseconds);
                    SignalDone();
                },
                ex =>
                {
                    if (ex is TimeoutException)
                        logger.LogError("Hosted hubs disposal timed out after 10 seconds ({elapsed}ms). Some hubs may not have disposed properly.",
                            totalStopwatch.ElapsedMilliseconds);
                    else
                        logger.LogError(ex, "Error during hosted hubs disposal after {elapsed}ms", totalStopwatch.ElapsedMilliseconds);
                    // Complete anyway — a wedged child must not block the owning hub's ShutDown.
                    SignalDone();
                });
    }

    private void SignalDone()
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnNext(Unit.Default);
        disposalCompleted.OnCompleted();
    }

}


using System.Collections.Immutable;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The registry's half of eager adoption (#3650): the moment a module bundle is published, tell
/// every registered consumer — so "ships" → "used" is minutes, not the consumer's next boot.
///
/// <para><b>Who is told.</b> Every <see cref="MeshWeaverInstance"/> registered on this registry that
/// is not disabled and recorded a <see cref="MeshWeaverInstance.HomeUrl"/> — the URL a consumer
/// hands over when it registers (<c>PluginCatalog:HomeUrl</c>). The registry does not know which
/// instances installed the package; it does not need to — the consumer's reconcile answers that from
/// its own install record, and a package that is not installed there costs one record read.</para>
///
/// <para><b>How.</b> One <see cref="ModulePublished"/> record, POSTed to each consumer's generic
/// webhook inbox at <see cref="ModulePublished.InboxRoute"/> — the same inbox every other event
/// reaches an installation through — signed GitHub-style when
/// <see cref="ModulePublished.BroadcastSecretConfigKey"/> is configured, unsigned otherwise (the
/// consumer treats the delivery as a wake-up and reads the truth from this registry's authenticated
/// index, so an unsigned delivery cannot make it land anything it would not have landed). A 404
/// means the consumer has not allowlisted the target; a 401 means the two halves of the secret
/// drifted — both are LOGGED per consumer, never thrown, because a publish that landed on the shelf
/// has succeeded whatever the fan-out did, and the consumer's safety net
/// (<see cref="PluginCatalogOptions.ReconcileSafetyNetInterval"/>) bounds what a lost delivery costs.</para>
///
/// <para>Reactive end to end: the instance listing is a mesh query as System, each POST runs on the
/// <c>Http</c> <see cref="IIoPool"/>, deliveries run one at a time and are isolated. Cold — nothing
/// runs until subscribed, and the publish route subscribes it detached from the publisher's own
/// response so a slow consumer never holds a CI job.</para>
/// </summary>
public sealed class ModulePublishedBroadcaster
{
    private readonly IMessageHub hub;
    private readonly ILogger<ModulePublishedBroadcaster>? logger;
    private readonly HttpClient http;
    private readonly IIoPool httpPool;

    /// <summary>The named <c>HttpClient</c> the broadcast sends through, so a host (or a test) can
    /// route it — the same seam <c>DeploymentReportService</c> uses.</summary>
    public const string HttpClientName = "module-published-broadcast";

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Creates the broadcaster over the mesh's services.</summary>
    public ModulePublishedBroadcaster(IMessageHub hub, ILogger<ModulePublishedBroadcaster>? logger = null)
    {
        this.hub = hub;
        this.logger = logger;
        httpPool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        http = hub.ServiceProvider.GetService<IHttpClientFactory>()?.CreateClient(HttpClientName)
               ?? new HttpClient { Timeout = DeliveryBudget };
    }

    /// <summary>One consumer's delivery outcome.</summary>
    /// <param name="InstanceId">The consumer's logical App ID.</param>
    /// <param name="Url">Where the delivery was POSTed.</param>
    /// <param name="Ok">Whether the consumer answered 2xx.</param>
    /// <param name="Detail">The status and body of a refusal, or null.</param>
    public sealed record Delivery(string InstanceId, string Url, bool Ok, string? Detail);

    /// <summary>
    /// Broadcasts <paramref name="published"/> to every reachable registered instance. Emits the
    /// per-consumer outcomes once, as one list; never throws — a listing that cannot be made, an
    /// unreachable consumer and a refusal are each an outcome to read.
    /// </summary>
    public IObservable<ImmutableArray<Delivery>> Broadcast(ModulePublished published)
    {
        var body = published.Serialize();
        var secret = hub.ServiceProvider.GetService<IConfiguration>()?[ModulePublished.BroadcastSecretConfigKey];
        return ListReachableInstances()
            .SelectMany(instances =>
            {
                if (instances.Length == 0)
                {
                    logger?.LogInformation(
                        "[ModulePublished] {Package} at {Version}: no registered instance records a home "
                        + "URL — nobody to tell; consumers reconcile on their safety net and at boot.",
                        published.Package, published.Version ?? "(unversioned)");
                    return Observable.Return(ImmutableArray<Delivery>.Empty);
                }
                return Observable
                    .Concat(instances.Select(instance => DeliverOne(instance, body, secret)))
                    .ToList()
                    .Select(results =>
                    {
                        var outcome = results.ToImmutableArray();
                        var failed = outcome.Where(d => !d.Ok).ToArray();
                        logger?.LogInformation(
                            "[ModulePublished] {Package} at {Version}: {Ok}/{Total} consumer(s) told{Failed}.",
                            published.Package, published.Version ?? "(unversioned)",
                            outcome.Length - failed.Length, outcome.Length,
                            failed.Length == 0
                                ? ""
                                : " — " + failed.Length + " refused or unreachable: "
                                  + string.Join("; ", failed.Select(d => $"{d.InstanceId} ({d.Detail})")));
                        return outcome;
                    });
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[ModulePublished] could not list the registered instances to tell about {Package} — "
                    + "consumers reconcile on their safety net and at boot. Cause: {Cause}",
                    published.Package, ex.Message);
                return Observable.Return(ImmutableArray<Delivery>.Empty);
            });
    }

    /// <summary>Every registered, enabled instance with a home URL — a mesh-wide query as System,
    /// because instances live under whichever user registered them (#3202: fan-out is opt-in).</summary>
    private IObservable<ImmutableArray<MeshWeaverInstance>> ListReachableInstances()
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var request = new MeshQueryRequest
        {
            Query = MeshWideQuery.Declare($"nodeType:{MeshWeaverInstanceNodeType.NodeType}"),
        };
        return accessService.RunAsSystem(() => meshService.Query(request))
            .Take(1)
            .Timeout(DeliveryBudget)
            .SelectMany(results => results
                // The routing index (MeshWeaverInstance/{hashPrefix}) shares the node type; an
                // instance RECORD lives under its owner. A query answers paths, not content, so
                // each record is then read once — the fleet is a handful of instances.
                .Where(result => result.State == MeshNodeState.Active
                                 && !string.Equals(result.Namespace, MeshWeaverInstanceNodeType.IndexNamespace, StringComparison.Ordinal))
                .Select(result => result.Path)
                .Distinct(StringComparer.Ordinal)
                .Select(path => accessService.RunAsSystem(() => hub.GetMeshNode(path, DeliveryBudget))
                    .Take(1)
                    .Select(node => node.ContentAs<MeshWeaverInstance>(hub.JsonSerializerOptions))
                    .Catch((Exception ex) =>
                    {
                        logger?.LogWarning(ex,
                            "[ModulePublished] could not read the instance record at {Path}; that consumer is not told.",
                            path);
                        return Observable.Return<MeshWeaverInstance?>(null);
                    }))
                .ToObservable()
                .Concat()
                .ToList())
            .Select(instances => instances
                .Where(instance => instance is { IsDisabled: false }
                                   && Uri.TryCreate(instance.HomeUrl?.Trim(), UriKind.Absolute, out var url)
                                   && url.Scheme is "http" or "https")
                .Select(instance => instance!)
                .ToImmutableArray());
    }

    // One consumer, already isolated: a non-2xx and a thrown exception both become Delivery(Ok:false).
    private IObservable<Delivery> DeliverOne(MeshWeaverInstance instance, string body, string? secret)
    {
        var url = instance.HomeUrl.Trim().TrimEnd('/') + ModulePublished.InboxRoute;
        return httpPool.Invoke(ct => PostAsync(instance.InstanceId, url, body, secret, ct))
            .Catch((Exception ex) => Observable.Return(new Delivery(instance.InstanceId, url, false, ex.Message)));
    }

    private async Task<Delivery> PostAsync(string instanceId, string url, string body, string? secret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(secret))
            request.Headers.Add(WebhookInbox.SignatureHeader, DeploymentReportService.Sign(body, secret));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return new Delivery(instanceId, url, true, null);
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new Delivery(instanceId, url, false,
            $"{(int)response.StatusCode}: {(detail.Length <= 200 ? detail : detail[..200] + "…")}");
    }
}

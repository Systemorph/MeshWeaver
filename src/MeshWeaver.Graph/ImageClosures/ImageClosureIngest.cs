using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.ImageClosures;

/// <summary>
/// 🚨 Turns the CD build's signed <c>image-closure</c> deliveries into <see cref="ImageClosure"/>
/// nodes (#4066). The CD build POSTs to the generic webhook inbox at
/// <see cref="ImageClosureNodes.InboxTarget"/>; this service drains <c>{target}/_Inbox</c>, re-verifies
/// each delivery's HMAC over the verbatim body with the target's own secret, validates the record
/// (<see cref="ImageClosureRecord.TryParse"/>), writes the node as System at
/// <see cref="ImageClosureNodes.PathOf"/>, and deletes the delivery.
///
/// <para><b>Armed only where it can verify.</b> Registered on every portal; it arms only when
/// <c>WebhookInbox:Targets</c> lists <see cref="ImageClosureNodes.InboxTarget"/> WITH a
/// <c>SecretConfigKey</c> that resolves. A target allowlisted without a secret is refused at Error —
/// an unsigned closure record would let anyone who can reach the endpoint write the data a gate
/// trusts.</para>
///
/// <para><b>Full-instance writes, no fold.</b> A record's content is the whole delivery; a
/// re-delivery of one digest replaces it (<see cref="IMeshService.CreateOrUpdateNode"/>). Tags are
/// therefore "what the writer knew", never a union — the identity is the digest.</para>
/// </summary>
public sealed class ImageClosureIngest(IMessageHub hub, ILogger<ImageClosureIngest> logger) : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();

    /// <summary>Deliveries this process already picked up, by path — the query re-emits until the
    /// delete lands. Instance state, immutable-swapped.</summary>
    private ImmutableHashSet<string> claimed = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);

    internal static IServiceCollection Register(IServiceCollection services) => services
        .AddSingleton<ImageClosureIngest>()
        .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ImageClosureIngest>());

    /// <summary>
    /// The secret config key the inbox target declares, or null when the target is not allowlisted
    /// (the ingest is not this instance's business) — and the empty string when it is allowlisted
    /// WITHOUT a key (a misconfiguration the caller refuses).
    /// </summary>
    /// <param name="configuration">The instance configuration.</param>
    public static string? SecretConfigKeyOf(IConfiguration? configuration)
    {
        var target = WebhookInbox.ReadTargets(configuration).FirstOrDefault(t => string.Equals(
            WebhookInbox.NormalizeTarget(t.Path), ImageClosureNodes.InboxTarget, StringComparison.Ordinal));
        return target is null ? null : target.SecretConfigKey ?? "";
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();
        var key = SecretConfigKeyOf(configuration);
        if (key is null)
        {
            logger.LogDebug(
                "[ImageClosure] {Target} is not an allowlisted webhook target on this instance — the CD "
                + "closure ingest stays unarmed (it runs on the instance the CD build delivers to).",
                ImageClosureNodes.InboxTarget);
            return Task.CompletedTask;
        }
        if (key.Length == 0)
        {
            logger.LogError(
                "[ImageClosure] {Target} is allowlisted WITHOUT a SecretConfigKey — refusing to arm: an "
                + "unsigned closure record would let anyone reaching /api/hooks write the data gates trust. "
                + "Declare WebhookInbox:Targets:N:SecretConfigKey=Hosting:PlatformWebhookSecret on this instance.",
                ImageClosureNodes.InboxTarget);
            return Task.CompletedTask;
        }

        var access = hub.ServiceProvider.GetService<AccessService>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var inbox = $"{ImageClosureNodes.InboxTarget}/{WebhookInbox.InboxContainer}";
        var query = $"path:{inbox} scope:children nodeType:{WebhookInbox.NodeType}";
        logger.LogInformation("[ImageClosure] armed — draining {Inbox} (secret key {Key})", inbox, key);

        // The index node is the inbox's OWNER: the endpoint refuses a delivery whose target node does
        // not exist, so it is ensured before anything is watched.
        subscriptions.Add(access.RunAsSystem(() => mesh.CreateOrUpdateNode(ImageClosureNodes.IndexNode()))
            .Select(_ => Unit.Default)
            .Concat(access.RunAsSystem(() => hub.GetQuery($"ImageClosure.Inbox:{hub.Address}", query))
                .SelectMany(nodes => (nodes ?? [])
                    .Where(n => !string.IsNullOrWhiteSpace(n.Path))
                    .OrderBy(n => n.ContentAs<WebhookEvent>(hub.JsonSerializerOptions)?.ReceivedAt ?? DateTimeOffset.MinValue)
                    .Where(n => ImmutableInterlocked.Update(ref claimed, set => set.Add(n.Path)))
                    .ToArray())
                .Select(node => Observable.Defer(() => Drain(hub, node, configuration?[key], logger))
                    .Select(_ => Unit.Default)
                    .Catch((Exception ex) =>
                    {
                        logger.LogWarning(ex,
                            "[ImageClosure] draining {Path} failed — it stays in the inbox and is retried at the next boot",
                            node.Path);
                        return Observable.Return(Unit.Default);
                    }))
                .Concat())
            .Subscribe(_ => { }, ex => logger.LogError(ex,
                "[ImageClosure] the inbox watch faulted — no further closure record is ingested in this process")));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Drains ONE delivery: verify, validate, write, delete. Emits the written node's path, or null
    /// when the delivery was refused (and deleted, naming why at Warning — a refusal never loops).
    /// </summary>
    /// <param name="hub">The hub whose mesh receives the record.</param>
    /// <param name="eventNode">The stored <see cref="WebhookEvent"/> node.</param>
    /// <param name="secret">The target's shared secret; null or empty refuses every delivery.</param>
    /// <param name="logger">Where refusals are named.</param>
    public static IObservable<string?> Drain(IMessageHub hub, MeshNode eventNode, string? secret, ILogger? logger)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var delivery = eventNode.ContentAs<WebhookEvent>(hub.JsonSerializerOptions);
        string? refusal = null;
        ImageClosure? closure = null;
        if (delivery is null)
            refusal = "not a WebhookEvent";
        else if (string.IsNullOrWhiteSpace(secret))
            refusal = "this instance holds no secret for the target";
        else if (!WebhookInbox.VerifyHmacSha256(
                     delivery.Headers.TryGetValue(WebhookInbox.SignatureHeader, out var sig) ? sig : null,
                     delivery.Body, secret))
            refusal = "the signature does not verify over the stored body";
        else
        {
            closure = ImageClosureRecord.TryParse(delivery.Body, delivery.ReceivedAt, out var why);
            if (closure is null)
                refusal = why;
        }

        var write = closure is null
            ? Observable.Return<string?>(null).Do(_ => logger?.LogWarning(
                "[ImageClosure] refused {Path}: {Why} — deleting it, nothing written", eventNode.Path, refusal))
            : access.RunAsSystem(() => mesh.CreateOrUpdateNode(ImageClosureNodes.ToNode(closure)))
                .Take(1)
                .Select(n => (string?)n.Path)
                .Do(path => logger?.LogInformation(
                    "[ImageClosure] recorded {Repository}@{Digest} ({Files} file(s) over {Platforms} platform(s)) at {Path}",
                    closure.Repository, closure.Digest, closure.FileCount, closure.Platforms.Count, path));

        // The delete is the acknowledgement — AFTER the write, so a process dying mid-pass leaves the
        // delivery for the next boot instead of losing it.
        return write.SelectMany(path => access.RunAsSystem(() => mesh.DeleteNode(eventNode.Path))
            .Take(1)
            .Select(_ => path));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => subscriptions.Dispose();
}

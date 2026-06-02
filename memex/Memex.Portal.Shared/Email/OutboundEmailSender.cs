using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Mesh-driven outbound sender — <b>no in-memory state</b>. The <c>Email Router</c> agent emits its
/// reply as an outbound <see cref="MeshWeaver.Mesh.Email"/> node (<c>Direction=Outbound, Status=New</c>)
/// in the parent email's namespace; this single hosted service watches for those via
/// <see cref="IMeshQueryCore"/>, claims each (New → Sending, the optimistic guard against double-send),
/// sends it through <see cref="IEmailSender"/>, and flips it to <see cref="EmailStatus.Sent"/> (or
/// <see cref="EmailStatus.Failed"/>). Dedup + restart-safety live entirely in the node's status.
///
/// <para>Reactive; the only Task boundary is the <see cref="IHostedService"/> contract. Self-skips
/// unless <c>Email:Enabled</c>.</para>
/// </summary>
public sealed class OutboundEmailSender(
    PortalApplication portalApp,
    EmailOptions options,
    ILogger<OutboundEmailSender>? logger = null) : IHostedService, IDisposable
{
    private readonly CompositeDisposable subscriptions = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger?.LogInformation("Email disabled — OutboundEmailSender idle");
            return Task.CompletedTask;
        }

        var hub = portalApp.Hub;
        var sp = hub.ServiceProvider;
        var query = sp.GetRequiredService<IMeshQueryCore>();
        var meshService = sp.GetRequiredService<IMeshService>();
        var accessService = sp.GetRequiredService<AccessService>();
        var emailSender = sp.GetRequiredService<IEmailSender>();
        var jsonOptions = hub.JsonSerializerOptions;

        // Live query: any outbound mail awaiting send. Emits the current set on change.
        subscriptions.Add(query
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:{EmailNodeType.NodeType} content.direction:Outbound content.status:New"), jsonOptions)
            .Select(change => change.Items)
            .Subscribe(
                items =>
                {
                    foreach (var node in items)
                        Send(node, meshService, accessService, emailSender, jsonOptions);
                },
                ex => logger?.LogWarning(ex, "OutboundEmailSender: query failed")));

        return Task.CompletedTask;
    }

    private void Send(
        MeshNode node, IMeshService meshService, AccessService accessService,
        IEmailSender emailSender, JsonSerializerOptions jsonOptions)
    {
        var email = EmailOf(node, jsonOptions);
        if (email is null || email.Direction != EmailDirection.Outbound || email.Status != EmailStatus.New)
            return;
        if (string.IsNullOrEmpty(email.To))
        {
            logger?.LogWarning("OutboundEmailSender: outbound {Path} has no recipient — marking Failed", node.Path);
            SetStatus(node, email, EmailStatus.Failed, meshService, accessService).Subscribe(_ => { }, _ => { });
            return;
        }

        // Claim: New → Sending (only if still New). The CAS lives in SetStatus's lambda, so a duplicate
        // emission that already flipped it is a no-op.
        SetStatus(node, email, EmailStatus.Sending, meshService, accessService)
            .SelectMany(claimed =>
            {
                // SetStatus returns the unchanged node when the CAS failed (already claimed) — skip.
                if ((EmailOf(claimed, jsonOptions)?.Status) != EmailStatus.Sending)
                    return Observable.Empty<bool>();
                var subject = email.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                    ? email.Subject : $"Re: {email.Subject}";
                return emailSender.SendEmail(email.To!, subject, email.Body)
                    .SelectMany(ok => SetStatus(claimed, EmailOf(claimed, jsonOptions)!,
                        ok ? EmailStatus.Sent : EmailStatus.Failed, meshService, accessService).Select(_ => ok));
            })
            .Subscribe(
                ok => logger?.LogInformation("OutboundEmailSender: {Path} → {To} sent={Sent}", node.Path, email.To, ok),
                ex =>
                {
                    logger?.LogWarning(ex, "OutboundEmailSender: send failed for {Path}", node.Path);
                    SetStatus(node, email, EmailStatus.Failed, meshService, accessService).Subscribe(_ => { }, _ => { });
                });
    }

    private static IObservable<MeshNode> SetStatus(
        MeshNode node, MeshWeaver.Mesh.Email current, EmailStatus to,
        IMeshService meshService, AccessService accessService) =>
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.UpdateNode(node with { Content = current with { Status = to } }));

    private static MeshWeaver.Mesh.Email? EmailOf(MeshNode n, JsonSerializerOptions? options) => n.Content switch
    {
        MeshWeaver.Mesh.Email e => e,
        JsonElement je => Safe(je, options),
        _ => null
    };

    private static MeshWeaver.Mesh.Email? Safe(JsonElement je, JsonSerializerOptions? options)
    {
        try { return JsonSerializer.Deserialize<MeshWeaver.Mesh.Email>(je.GetRawText(), options); }
        catch { return null; }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() => subscriptions.Dispose();
}

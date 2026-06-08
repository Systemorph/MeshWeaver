using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Email;

/// <summary>
/// Mesh-driven invitation emailer — node-state driven, NO in-memory dedup. Watches Pending
/// <see cref="Invitation"/> nodes (Admin partition) that have not been emailed yet
/// (<see cref="Invitation.EmailSentAt"/> == null) via <see cref="IMeshQueryCore"/>, sends the
/// "You've been invited" email through <see cref="IEmailSender"/>, and stamps
/// <c>EmailSentAt</c> so it never re-sends.
///
/// <para>Decouples the invite email from the creation entry point: an invitation created from the
/// Invitations settings tab, from MCP (raw <c>create</c>), or from a REST call all get emailed
/// exactly once. Mirrors <see cref="OutboundEmailSender"/>; the only Task boundary is the
/// <see cref="IHostedService"/> contract. Self-skips unless <c>Email:Enabled</c>.</para>
/// </summary>
public sealed class InvitationEmailSender(
    IServiceProvider rootServices,
    IHostApplicationLifetime lifetime,
    EmailOptions options,
    IConfiguration configuration,
    ILogger<InvitationEmailSender>? logger = null) : IHostedService, IDisposable
{
    private const string InviteSubject = "You've been invited to Memex";
    private readonly CompositeDisposable subscriptions = new();
    private IServiceScope? scope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger?.LogInformation("Email disabled — InvitationEmailSender idle");
            return Task.CompletedTask;
        }

        // Defer mesh access until the host is fully started (Orleans + mesh hub come up as
        // hosted services too) — same rationale as OutboundEmailSender.
        lifetime.ApplicationStarted.Register(BeginWatching);
        return Task.CompletedTask;
    }

    private void BeginWatching()
    {
        try
        {
            scope = rootServices.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<PortalApplication>().Hub;
            var sp = hub.ServiceProvider;
            var query = sp.GetRequiredService<IMeshQueryCore>();
            var meshService = sp.GetRequiredService<IMeshService>();
            var accessService = sp.GetRequiredService<AccessService>();
            var emailSender = sp.GetRequiredService<IEmailSender>();
            var jsonOptions = hub.JsonSerializerOptions;
            var baseUrl = configuration["Portal:BaseUrl"]
                          ?? configuration["PublicBaseUrl"]
                          ?? configuration["Email:WebhookBaseUrl"];

            // Live query: ALL invitations — filter Pending + not-yet-emailed in Send. We do NOT
            // filter `content.status:Pending` in the query: a Pending invitation's status is the
            // enum default (0) and is OMITTED from the stored JSON, so that filter would never
            // match (same reason OnboardingMiddleware/InvitationService filter status in code).
            // IMeshQueryCore = the no-access-control core path (infra read).
            logger?.LogInformation("InvitationEmailSender: watching invitations (baseUrl={BaseUrl})", baseUrl ?? "(none)");
            subscriptions.Add(query
                // namespace:Admin scopes the query to the Admin partition. A path-less
                // nodeType:Invitation query goes cross-schema, which intentionally EXCLUDES
                // the admin schema (see PostgreSqlSchemaInitializer.searchable_schemas), so
                // it would never see invitations. Runs as System (IMeshQueryCore + MeshQuery's
                // System stamp), so no access-control filtering. See AccessControl.md.
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{InvitationNodeType.PartitionName} nodeType:{InvitationNodeType.NodeType}"), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var node in items)
                            Send(node, meshService, accessService, emailSender, jsonOptions, baseUrl);
                    },
                    ex => logger?.LogWarning(ex, "InvitationEmailSender: query failed")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "InvitationEmailSender: failed to start watching invitations");
        }
    }

    private void Send(
        MeshNode node, IMeshService meshService, AccessService accessService,
        IEmailSender emailSender, JsonSerializerOptions jsonOptions, string? baseUrl)
    {
        var invitation = InvitationService.TryGetInvitation(node, jsonOptions);
        if (invitation is null
            || invitation.Status != InvitationStatus.Pending
            || invitation.EmailSentAt is not null
            || string.IsNullOrWhiteSpace(invitation.Email))
            return;

        // Claim FIRST: stamp EmailSentAt before sending so a duplicate query emission (or a
        // second replica) doesn't re-send. On send failure we clear the stamp so a later tick
        // retries. (Single-writer per node on the owning hub; last-write-wins is acceptable for
        // the rare multi-replica race — worst case one duplicate email, never a lost one.)
        var claimedAt = DateTimeOffset.UtcNow;
        SetEmailSentAt(node, invitation, claimedAt, meshService, accessService)
            .SelectMany(_ => emailSender.SendEmail(invitation.Email!, InviteSubject, BuildInviteEmailHtml(baseUrl)))
            .Subscribe(
                ok => logger?.LogInformation(
                    "InvitationEmailSender: {Email} emailed (sent={Sent})", invitation.Email, ok),
                ex =>
                {
                    logger?.LogWarning(ex, "InvitationEmailSender: send failed for {Email}", invitation.Email);
                    // Roll back the claim so the invitation is retried on the next emission.
                    SetEmailSentAt(node, invitation, null, meshService, accessService)
                        .Subscribe(_ => { }, _ => { });
                });
    }

    private static IObservable<MeshNode> SetEmailSentAt(
        MeshNode node, Invitation current, DateTimeOffset? to,
        IMeshService meshService, AccessService accessService) =>
        // System identity: invitations live in the Admin partition that application identities
        // can't write to directly (same infrastructure-write pattern as InvitationService).
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.UpdateNode(node with { Content = current with { EmailSentAt = to } }));

    private static string BuildInviteEmailHtml(string? baseUrl)
    {
        var link = string.IsNullOrEmpty(baseUrl)
            ? ""
            : $"<p style=\"margin:16px 0;\"><a href=\"{System.Net.WebUtility.HtmlEncode(baseUrl)}\" " +
              "style=\"background:#2563eb;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;\">"
              + "Open Memex</a></p>"
              + $"<p style=\"color:#888;font-size:12px;\">{System.Net.WebUtility.HtmlEncode(baseUrl)}</p>";
        return "<p>You've been invited to Memex.</p>"
               + "<p>Sign in with this email address to get started.</p>"
               + link;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}

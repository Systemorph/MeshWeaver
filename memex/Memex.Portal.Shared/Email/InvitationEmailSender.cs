using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Data;
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
/// Mesh-driven invitation emailer. Watches Pending <see cref="Invitation"/> nodes (Admin
/// partition) that have not been emailed yet (<see cref="Invitation.EmailSentAt"/> == null) via
/// <see cref="IMeshQueryCore"/>, sends the "You've been invited" email through
/// <see cref="IEmailSender"/>, and stamps <c>EmailSentAt</c> so it never re-sends.
///
/// <para><b>Two-layer de-dup.</b> Node-state (<c>EmailSentAt</c>) is the durable, cross-restart
/// guard. But the live query's snapshot LAGS the claim write — after we stamp <c>EmailSentAt</c>
/// the query keeps re-emitting the stale (null) node, and because each node's claim re-emits the
/// WHOLE set, a single batch would otherwise re-claim+re-send each invitation many times before
/// propagation. So an in-process single-claim guard (<see cref="claiming"/>, an INSTANCE field —
/// mesh/process-scoped, never static) makes the first claim per path win; later stale emissions
/// short-circuit. Released only on send failure so a later tick retries.</para>
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
    private const string GenericSubject = "You've been invited to Memex";
    // Cap on resolving the invited Space's display name for the email — a nice-to-have, so we fall
    // back to the raw space path (never block the send) if the cross-partition read is slow.
    private static readonly TimeSpan SpaceLookupTimeout = TimeSpan.FromSeconds(10);
    private readonly CompositeDisposable subscriptions = new();
    // In-process single-claim guard: path → claimed. Instance (mesh/process-scoped), never static.
    // Prevents the live-query snapshot lag from re-claiming+re-sending the same invitation while
    // its EmailSentAt write propagates. See class summary; released on send failure to allow retry.
    private readonly ConcurrentDictionary<string, byte> claiming = new();
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
            var workspace = hub.GetWorkspace();
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
                // PATH-scoped to Admin/Invitation. Routing is by the path's FIRST SEGMENT
                // (PostgreSqlPartitionedMeshQuery.FirstSegment → schema), so `path:Admin/…`
                // routes to the admin schema. A `namespace:Admin`-only query has NO path, so it
                // fans out cross-schema — and the admin schema is intentionally EXCLUDED from
                // that fan-out (PostgreSqlSchemaInitializer.searchable_schemas) — so it would
                // never see invitations. (`namespace:Admin` is also exact-match and would miss
                // the `Admin/Invitation` namespace regardless.) scope:children = the invitation
                // slugs directly under Admin/Invitation. Runs as System (IMeshQueryCore +
                // MeshQuery's System stamp) — no access-control filtering. See AccessControl.md.
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{InvitationNodeType.Namespace} scope:children nodeType:{InvitationNodeType.NodeType}"), jsonOptions)
                .Select(change => change.Items)
                .Subscribe(
                    items =>
                    {
                        foreach (var node in items)
                            Send(node, meshService, accessService, emailSender, workspace, jsonOptions, baseUrl);
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
        IEmailSender emailSender, IWorkspace workspace, JsonSerializerOptions jsonOptions, string? baseUrl)
    {
        var invitation = InvitationService.TryGetInvitation(node, jsonOptions);
        if (invitation is null
            || invitation.Status != InvitationStatus.Pending
            || invitation.EmailSentAt is not null
            || string.IsNullOrWhiteSpace(invitation.Email))
            return;

        // In-process single-claim: the live query re-emits the STALE (EmailSentAt=null) node many
        // times before our claim write propagates back into its snapshot (and every node's claim
        // re-emits the whole set), so without this guard one batch re-sends each invitation many
        // times. TryAdd makes the first claim per path win; we release only on send failure.
        if (!claiming.TryAdd(node.Path, 0))
            return;

        // Claim FIRST: stamp EmailSentAt before sending so a duplicate query emission (or a
        // second replica) doesn't re-send. On send failure we clear the stamp so a later tick
        // retries. (Single-writer per node on the owning hub; last-write-wins is acceptable for
        // the rare multi-replica race — worst case one duplicate email, never a lost one.)
        var claimedAt = DateTimeOffset.UtcNow;
        SetEmailSentAt(node, invitation, claimedAt, meshService, accessService)
            .SelectMany(_ => ResolveSpaceName(invitation, workspace, accessService))
            .SelectMany(spaceName =>
            {
                var (subject, html) = BuildInviteEmail(invitation, spaceName, baseUrl);
                return emailSender.SendEmail(invitation.Email!, subject, html);
            })
            .Subscribe(
                ok => logger?.LogInformation(
                    "InvitationEmailSender: {Email} emailed (sent={Sent})", invitation.Email, ok),
                ex =>
                {
                    logger?.LogWarning(ex, "InvitationEmailSender: send failed for {Email}", invitation.Email);
                    // Release the in-process claim + roll back the stamp so a later tick retries.
                    claiming.TryRemove(node.Path, out _);
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

    /// <summary>
    /// Resolves the invited Space's display name for a space-scoped invitation (reads the Space
    /// node's <see cref="MeshNode.Name"/> as System, since this hosted service has no user context).
    /// Emits <c>null</c> for a deployment-wide invitation (no <see cref="Invitation.SpacePath"/>),
    /// and falls back to the raw space path if the read is slow or the node isn't visible — the
    /// name is cosmetic, so we never block or fail the send on it.
    /// </summary>
    private static IObservable<string?> ResolveSpaceName(
        Invitation invitation, IWorkspace workspace, AccessService accessService)
    {
        if (string.IsNullOrWhiteSpace(invitation.SpacePath))
            return Observable.Return<string?>(null);

        var spacePath = invitation.SpacePath!.Trim();
        return Observable.Using(
            accessService.ImpersonateAsSystem,
            _ => workspace.GetMeshNodeStream(spacePath)
                .Where(n => n is not null)
                .Select(n => string.IsNullOrWhiteSpace(n!.Name) ? spacePath : n.Name)
                .Take(1)
                .Timeout(SpaceLookupTimeout, Observable.Return<string?>(spacePath))
                .Catch(Observable.Return<string?>(spacePath)));
    }

    /// <summary>
    /// Builds the invitation email. When the invitation targets a Space
    /// (<see cref="Invitation.SpacePath"/> set) it addresses the Space by <paramref name="spaceName"/>
    /// (falling back to the path) and links straight to it (<c>{baseUrl}/{SpacePath}</c>); otherwise
    /// it renders the generic deployment-wide invite. Pure + static so it is directly unit-testable.
    /// </summary>
    internal static (string Subject, string Html) BuildInviteEmail(
        Invitation invitation, string? spaceName, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(invitation.SpacePath))
            return (GenericSubject, BuildGenericHtml(baseUrl));

        var spacePath = invitation.SpacePath!.Trim();
        var display = string.IsNullOrWhiteSpace(spaceName) ? spacePath : spaceName!.Trim();
        var spaceUrl = string.IsNullOrEmpty(baseUrl)
            ? null
            : $"{baseUrl!.TrimEnd('/')}/{spacePath.TrimStart('/')}";
        return ($"You've been invited to {display}", BuildSpaceHtml(display, spaceUrl));
    }

    private static string BuildGenericHtml(string? baseUrl)
        => MeshWeaver.Graph.EmailTemplate.Build(
            heading: "You've been invited to Memex",
            paragraphs: ["You've been invited to Memex — a shared workspace for your team's content and agents."],
            ctaLabel: string.IsNullOrEmpty(baseUrl) ? null : "Open Memex",
            ctaUrl: string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            footerNote: "Sign in with this email address to get started.");

    private static string BuildSpaceHtml(string spaceName, string? spaceUrl)
        => MeshWeaver.Graph.EmailTemplate.Build(
            heading: $"You've been invited to {spaceName}",
            paragraphs: [$"You've been invited to \"{spaceName}\" on Memex."],
            ctaLabel: string.IsNullOrEmpty(spaceUrl) ? null : $"Open {spaceName}",
            ctaUrl: string.IsNullOrEmpty(spaceUrl) ? null : spaceUrl,
            footerNote: "Sign in with this email address to get started.");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        subscriptions.Dispose();
        scope?.Dispose();
    }
}

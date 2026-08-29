using Microsoft.Extensions.Configuration;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.SelfUpdate;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for the platform auto-update strategy (<c>Admin/UpdatePolicy</c>). Edits the
/// policy through the STANDARD node-content editor bound directly to the node stream (the
/// <c>Policy</c> enum renders as a dropdown — no /data replica, no save subscription), shows the
/// running version + the latest tag the poller has seen, and offers a manual "apply now" for
/// installs that can self-patch.
///
/// <para>Platform admins only: the menu entry is a seeded <c>UiContribution</c> node with
/// <c>Gates.AdminOnly</c> (<see cref="PlatformSettingsTabAreas"/>), and the
/// <c>SettingsUpdatePolicy</c> layout area re-asserts the admin gate for direct URLs.</para>
/// </summary>
public static class UpdatePolicySettingsTab
{
    public const string TabId = "UpdatePolicy";
    private const string ResultId = "updatePolicyResult";

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2(host.Localize("ui.platformUpdates")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "The platform self-updates per the strategy below. **Continuous** rolls to the newest build " +
            "(including build-numbered continuous builds); **Stable** rolls only to clean releases; " +
            "**None** disables auto-update. **Only update to CI-verified (green) builds** (default on) " +
            "keeps the install on builds that passed CI — turn it off to also accept unverified edge builds. " +
            "On Kubernetes the portal patches its own deployment; elsewhere the latest version is surfaced " +
            "for a manual update."));

        // Running version (the installed platform version baked into the binary).
        stack = stack.WithView(Controls.Markdown(
            $"**Running version:** `{ShippedReleaseSeed.InstalledPlatformVersion}`"));

        // Policy editor — bound DIRECTLY to Admin/UpdatePolicy. EnsureExists (create-on-absent, as
        // System) before binding so the editor binds to an existing node.
        stack = stack.WithView((h, _) => UpdatePolicyNodeType
            .EnsureExists(h.Hub, h.Hub.ServiceProvider.GetService<AccessService>(), UpdatePolicyKind.Continuous)
            .Select(_ => (UiControl?)MeshNodeContentEditorControl.ForType(
                UpdatePolicyNodeType.NodePath, typeof(UpdatePolicyContent)))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingUpdatePolicy"))));

        // Live status: the latest available tag, when last checked, and — when the combo gate has
        // verified that tag against THIS instance's module set (mw-combo-verify) — its verdict. A
        // red verdict replaces the eternal "update available" with "cannot update to X" naming
        // every failing module (Doc/Architecture/CandidateReleaseProtocol → "Report the verdict
        // where an admin looks").
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
            .Select(node => (UiControl?)Controls.Markdown(
                StatusMarkdown(
                    UpdatePolicyNodeType.Parse(node, h.Hub.JsonSerializerOptions),
                    (key, args) => h.Localize(key, args),
                    h.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId())))
            .StartWith((UiControl?)Controls.Markdown("")));

        // Manual apply (installs that can self-patch). Reads the latest tag, then patches via the Http pool.
        stack = stack.WithView(Controls.Button(host.Localize("ui.applyUpdateNow"))
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var h = ctx.Host;
                var updater = h.Hub.ServiceProvider.GetService<IDeploymentUpdater>();
                if (updater is null || !updater.CanPatch)
                {
                    h.UpdateData(ResultId, "This install cannot self-patch (not running in Kubernetes).");
                    return Task.CompletedTask;
                }
                var pool = h.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                           ?? IoPool.Unbounded;
                h.Hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath).Take(1).Subscribe(node =>
                {
                    var tag = UpdatePolicyNodeType.Parse(node, h.Hub.JsonSerializerOptions).LatestAvailableTag;
                    if (string.IsNullOrEmpty(tag))
                    {
                        h.UpdateData(ResultId, "No newer version has been detected yet.");
                        return;
                    }
                    // 🚨 The manual roll honours the SAME release-availability gate as the poller
                    // (#1754). A gate only the unattended path respects is not a gate — and this
                    // button is exactly the moment an operator, seeing an update that never
                    // applied, would force the roll the poller refused for good reason.
                    //
                    // 🚨 An UNWIRED gate is a HOLD here too. It used to resolve to NotEnforced,
                    // which is `IsUpdatable: true` — so the one host where nothing checks anything
                    // was also the one host where this button never refused. NotEnforced is the
                    // single stated applicability exemption (a deployment that consumes no CI
                    // bakes); a missing registration is not an exemption, it is the absence of a
                    // verdict, and it must not render as a pass.
                    //
                    // 🚨 …and the hold is SCOPED to what is actually unverifiable. On a deployment
                    // that consumes no CI bakes a registered gate answers NotEnforced anyway, so
                    // its absence is the same answer reached from configuration — not an
                    // unanswered question. Holding there would freeze an install the gate was
                    // never going to protect.
                    var gate = h.Hub.ServiceProvider.GetService<ReleaseAvailabilityService>();
                    var decision = gate is not null
                        ? gate.IsUpdatable(tag)
                        : Observable.Return(
                            ReleaseAvailabilityService.NotApplicableReason(
                                h.Hub.ServiceProvider.GetService<IConfiguration>()) is { } notApplicable
                                ? UpdatabilityVerdict.NotEnforced(notApplicable)
                                : UpdatabilityVerdict.Unavailable(
                                    "no release-availability gate is registered on this install, so "
                                    + "nothing could check whether the packages it deploys have "
                                    + "usable artifacts for this release — that is a hold, not "
                                    + "clearance to proceed"));
                    decision.Subscribe(verdict =>
                    {
                        if (!verdict.IsUpdatable)
                        {
                            h.UpdateData(ResultId,
                                h.Localize("ui.updateHeldManual", tag) + "\n\n> " + verdict.HoldReason);
                            return;
                        }
                        pool.Invoke(ct => updater.PatchToVersionAsync(tag, ct)).Subscribe(
                            _ => h.UpdateData(ResultId, $"Rolling the platform to {tag}…"),
                            ex => h.UpdateData(ResultId, $"Update failed: {ex.Message}"));
                    });
                });
                return Task.CompletedTask;
            }));

        // Result line.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(msg => (UiControl?)(string.IsNullOrEmpty(msg)
                ? Controls.Stack.WithWidth("100%")
                : Controls.Markdown(msg)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    /// <summary>How many caveat lines a NotVerifiable verdict renders before "… and N more".
    /// Failing MODULES are never truncated — breadth-complete reporting is the gate's point.</summary>
    private const int MaxRenderedCaveats = 6;

    /// <summary>
    /// The status line(s): latest available tag + check time, joined with the combo gate's verdict
    /// for that tag when one is recorded. Pure and localizer-seamed so the three verdict renderings
    /// are pinned by unit tests. Module ids, tags, digests, and the gate's failure/caveat
    /// diagnostics render VERBATIM — they are machine text, the same rule as compiler output
    /// (<c>InstanceComboReader.ProvenanceCaveat</c> documents the choice); every label around them
    /// is localized.
    /// </summary>
    /// <param name="zoneId">
    /// The viewer's IANA zone (<c>AccessService.ViewerZoneId()</c>); null renders UTC. Optional so
    /// the existing callers and tests compile unchanged — but the production caller passes it,
    /// because an instant formatted in UTC is simply the wrong time for the person reading it
    /// (#2302), and on Blazor Server the ambient culture is the CONTAINER's, identical for every
    /// simultaneous viewer.
    /// </param>
    internal static string StatusMarkdown(
        UpdatePolicyContent content, Func<string, object?[], string> localize, string? zoneId = null)
    {
        // 🚨 "No newer version detected yet" is a CLAIM, and until #2553 this surface made it
        // without evidence. An install that has checked hourly and found nothing and an install
        // whose checker has not run since the pod started produce the identical empty
        // LatestAvailableTag — and the second is the one that matters, because it is how memex sat
        // three builds behind for 7 h while this line reassured whoever read it. The two are now
        // different sentences, and the honest one comes first: without a completed check there is
        // nothing to report but the absence of a check.
        if (string.IsNullOrEmpty(content.LatestAvailableTag))
            return content.LastCheckedAt is { } checkedAt
                ? localize("ui.updateNoNewerVersion", [])
                    + " " + localize("ui.updateCheckedAt",
                        [DisplayTimeExtensions.ToDisplayTime(checkedAt, zoneId).ToString("yyyy-MM-dd HH:mm")])
                : localize("ui.updateNeverChecked", []);

        var tag = content.LatestAvailableTag!;
        var available = localize("ui.updateLatestAvailable", [tag])
            + (content.CheckedAt is { } at
                ? " " + localize("ui.updateCheckedAt", [DisplayTimeExtensions.ToDisplayTime(at, zoneId).ToString("yyyy-MM-dd HH:mm")])
                : "");

        // 🚨 The availability hold (#1754) is reported BEFORE the combo verdict, because it is the
        // reason this install is not moving RIGHT NOW. A hold that only showed up in the logs would
        // leave an operator reading "update available" for weeks with nothing explaining why the
        // version never changes — the silent freeze this gate must never become.
        if (content.IsHeld(tag))
            available += "\n\n"
                + localize(
                    content.HeldIndeterminate ? "ui.updateHeldUnknown" : "ui.updateHeld", [tag])
                + (string.IsNullOrEmpty(content.HeldReason)
                    ? ""
                    : "\n\n> " + Sanitize(content.HeldReason!))
                + (content.HeldAt is { } heldAt
                    ? "\n\n" + localize("ui.updateHeldAt", [DisplayTimeExtensions.ToDisplayTime(heldAt, zoneId).ToString("yyyy-MM-dd HH:mm")])
                    : "");

        var verdict = content.VerificationFor(tag);
        if (verdict is null)
            return available;

        var verifiedAt = localize("ui.updateVerifiedAtLine",
            [DisplayTimeExtensions.ToDisplayTime(verdict.VerifiedAt, zoneId).ToString("yyyy-MM-dd HH:mm"), verdict.ImageDigest ?? "?"]);
        // 🚨 Caveats are surfaced on EVERY verdict — ComboVerification.Caveats documents them as
        // mandatory-to-surface. A Green over a moving pin, or a Red whose input diverged, must
        // never render as an unqualified answer (Copilot review on #1099).
        return verdict.Verdict switch
        {
            ComboVerdictKind.Green =>
                available + "\n\n"
                + localize("ui.updateVerifiedGreen", [verdict.Modules.Count])
                + " " + verifiedAt
                + CaveatBlock(verdict, localize),
            ComboVerdictKind.Red =>
                localize("ui.updateBlocked", [tag]) + "\n\n"
                + FailedModuleList(verdict, localize)
                + CaveatBlock(verdict, localize) + "\n\n" + verifiedAt,
            _ =>
                localize("ui.updateNotVerifiable", [tag]) + "\n\n"
                + NotVerifiedModuleList(verdict)
                + CaveatList(verdict, localize) + "\n\n" + verifiedAt,
        };
    }

    /// <summary>Every failing module — never truncated — with its first failure line (and a count
    /// of the rest).</summary>
    private static string FailedModuleList(
        ComboVerification verdict, Func<string, object?[], string> localize) =>
        string.Join("\n", verdict.FailedModules.Select(module =>
        {
            var first = module.Failures.Count > 0 ? Sanitize(module.Failures[0]) : "";
            var more = module.Failures.Count > 1
                ? " " + localize("ui.updateMoreFailures", [module.Failures.Count - 1])
                : "";
            return $"- **{module.ModuleId}** — {first}{more}";
        }));

    /// <summary>The per-module reasons of a NotVerifiable verdict: every module the gate could
    /// not evaluate that carries a named reason (a refusal, a fetch failure, a missing root, not
    /// discovered) — never truncated, so one blocked module does not hide another. Modules that
    /// merely rode along (materialised fine, gate never ran) carry no reason and are not
    /// listed.</summary>
    private static string NotVerifiedModuleList(ComboVerification verdict)
    {
        var lines = verdict.Modules
            .Where(m => m.Outcome == ModuleVerificationOutcome.NotVerified && m.Failures.Count > 0)
            .Select(m => $"- **{m.ModuleId}** — {Sanitize(m.Failures[0])}")
            .ToList();
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n\n";
    }

    /// <summary>The caveats as a labeled block appended to a Green/Red verdict; empty when there
    /// are none.</summary>
    private static string CaveatBlock(
        ComboVerification verdict, Func<string, object?[], string> localize) =>
        verdict.Caveats.Count == 0
            ? ""
            : "\n\n" + localize("ui.updateCaveats", []) + "\n\n" + CaveatList(verdict, localize);

    private static string CaveatList(
        ComboVerification verdict, Func<string, object?[], string> localize)
    {
        var lines = verdict.Caveats
            .Take(MaxRenderedCaveats)
            .Select(caveat => $"- {Sanitize(caveat)}")
            .ToList();
        if (verdict.Caveats.Count > MaxRenderedCaveats)
            lines.Add("- " + localize("ui.updateMoreItems", [verdict.Caveats.Count - MaxRenderedCaveats]));
        return string.Join("\n", lines);
    }

    /// <summary>One markdown-safe line out of a multi-line diagnostic, bounded.</summary>
    private static string Sanitize(string diagnostic)
    {
        var flat = diagnostic.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}

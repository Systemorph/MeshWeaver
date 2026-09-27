using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.SelfUpdate;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab <b>Control lane</b>: where a global administrator of THIS instance
/// GENERATES its announcement key (shown once with its fingerprint, then sent to the control
/// instance's operators over a secure channel, who register it), checks the fingerprint, and tests
/// the pairing. A key handed over the other way can also be pasted and saved. The self-updater signs
/// its hand-over to the control instance with that key (<see cref="SelfUpdateHandover"/>). See
/// <c>Doc/Architecture/SelfUpdateAnnouncementKey</c>.
///
/// <para>Built on the platform's write-only secret control (<see cref="WriteOnlySecretSection"/>):
/// a generated key is shown ONCE; a pasted key is typed into a password box. Either is saved
/// encrypted (<see cref="InstanceSecrets"/>) and never shown again. The page shows only whether a key is present, its fingerprint, who saved it and
/// when, and the last announcement's result.</para>
///
/// <para>🚨 <b>Live, no restart.</b> The self-updater reads the key at every announcement, so a key
/// saved here is used by <b>Test connection</b> and by the next announcement at once.</para>
/// </summary>
public static class ControlLaneSettingsTab
{
    /// <summary>The tab id — a tab of the Admin app (<c>/Admin/Settings/ControlLane</c>); the old
    /// <c>/GlobalSettings/ControlLane</c> deep link redirects there.</summary>
    public const string TabId = "ControlLane";

    /// <summary>The section id (namespaces its layout data).</summary>
    internal const string SectionId = "controlLaneKey";

    /// <summary>Layout-data key of the one-line result of Test connection.</summary>
    internal const string TestResultId = "controlLaneTestResult";

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        const string key = SelfUpdateHandover.SecretKey;
        host.UpdateData(TestResultId, "");
        // Cold, one subscription per view: the catalog replays its current state to each.
        var status = InstanceSecrets.ObserveStatus(host.Hub, key);

        var test = Controls.Button(host.Localize("ui.controlLaneTest"))
            .WithClickAction(ctx =>
            {
                RunTest(ctx.Host);
                return Task.CompletedTask;
            });

        stack = stack
            .WithView(Controls.H2(host.Localize("ui.controlLane")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(WriteOnlySecretSection.Render(host, new SecretSectionSpec(SectionId, status.Select(s => s.Secret))
            {
                Intro = host.Localize("ui.mdControlLaneIntro"),
                InputLabel = host.Localize("ui.controlLaneKeyLabel"),
                Placeholder = host.Localize("ui.controlLaneKeyPlaceholder"),
                Detail = status.Select(s => DetailMarkdown(s, (k, a) => host.Localize(k, a), host.ViewerLocale())),
                GenerateLabel = host.Localize("ui.controlLaneGenerate"),
                RegenerateLabel = host.Localize("ui.controlLaneRegenerate"),
                Verbs = new SecretSectionVerbs
                {
                    // The key is generated HERE, on this instance, on the server, and shown ONCE with
                    // its fingerprint; the administrator hands it to Systemorph over a secure
                    // channel, and Systemorph registers it on the control instance. Until then the
                    // control instance refuses this instance's announcements (Test connection says so).
                    Generate = () => InstanceSecrets.Generate(host.Hub, key)
                        .Select(g => new SecretGenerated(g.Value, g.Status.Secret, host.Localize("ui.controlLaneGeneratedNote"))),
                    Save = value => InstanceSecrets.Set(host.Hub, key, value).Select(s => s.Secret),
                },
                ExtraButtons = [test],
            }));

        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(TestResultId)
            .Select(msg => (UiControl?)(string.IsNullOrEmpty(msg)
                ? Controls.Stack.WithWidth("100%")
                : Controls.Markdown(msg)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    /// <summary>Test connection: a signed, verify-only test to the control instance; its verdict is shown and recorded.</summary>
    private static void RunTest(LayoutAreaHost host)
    {
        host.UpdateData(TestResultId, host.Localize("ui.controlLaneTesting"));
        new SelfUpdateHandover(host.Hub).Test()
            .Subscribe(
                outcome => host.UpdateData(TestResultId,
                    TestMarkdown(outcome, (k, a) => host.Localize(k, a), host.ViewerLocale())),
                // A keyed refusal renders in the viewer's language; a transport failure is the
                // network stack's own words, shown verbatim (LocalizableText.Verbatim's rule).
                ex => host.UpdateData(TestResultId,
                    host.Localize("ui.controlLaneTestFailed", WriteOnlySecretSection.Describe(ex, host.ViewerLocale()))));
    }

    /// <summary>The test's verdict as one paragraph. A refusal reads as a MISMATCH to re-enter. Pure over the localizer.</summary>
    internal static string TestMarkdown(SelfUpdateHandover.TestOutcome outcome, Func<string, object?[], string> localize, string? locale) =>
        outcome.Accepted
            ? outcome.Sender is { } sender
                ? localize("ui.controlLaneTestMatch", [sender])
                : localize("ui.controlLaneTestFleet", [])
            : localize("ui.controlLaneTestMismatch", [outcome.Text.Localize(locale)]);

    /// <summary>
    /// What the standard status does not say: that the key comes from the deployment configuration
    /// rather than this page, that no key means no hand-over, and the last announcement's result.
    /// Names and instants only — never a value. Pure over the localizer.
    /// </summary>
    internal static string DetailMarkdown(InstanceSecretStatus status, Func<string, object?[], string> localize, string? locale)
    {
        var lines = new List<string>();
        if (status.Origin == InstanceSecretStatus.FromConfiguration)
            lines.Add(localize("ui.controlLaneKeyMounted", []));
        else if (!status.Secret.Present || status.Secret.Enabled == false || status.Secret.Deleted)
            lines.Add(localize("ui.controlLaneKeyNone", []));
        if (status.LastUsedAt is { } used)
            lines.Add(localize(status.LastUseOk == true ? "ui.controlLaneLastOk" : "ui.controlLaneLastFailed",
                [WriteOnlySecretSection.Stamp(used), status.LastUseResult?.Localize(locale) ?? ""]));
        return string.Join("\n\n", lines);
    }
}

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Minimal, secret-safe detail layout for <c>ModelProvider</c> nodes: just the editable endpoint URL
/// and a write-only "Enter Key" button — nothing else. The generic node editor rendered the provider
/// <c>ApiKey</c> in PLAINTEXT (a key leak) plus a row of Edit/Copy/Move/Delete buttons; this custom
/// <c>Overview</c> replaces both. The key is NEVER displayed — it is only SET via a masked password
/// dialog (an inline equivalent of <c>ModelProviderService.RotateKey</c>, which lives in a higher
/// project this assembly can't reference: encrypt-at-rest then own-node <c>stream.Update</c> +
/// force-persist). Mirrors <see cref="ApiTokenLayoutAreas"/>.
/// </summary>
public static class ModelProviderLayoutAreas
{
    private const string OverviewArea = "Overview";

    public static MessageHubConfiguration AddModelProviderViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            // The /Edit route renders the SAME minimal form — the generic EditNode editor is what
            // "did not work at all" (and leaked the key); point it at our view too.
            .WithView(MeshNodeLayoutAreas.EditArea, Overview));

    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ModelProviderConfiguration cfg)
                    return (UiControl?)Controls.Markdown("_No provider data._");

                var path = node.Path;
                var title = cfg.Label ?? cfg.Provider ?? path.Split('/').Last();
                var keyState = string.IsNullOrEmpty(cfg.ApiKey) ? "not set" : "set ✓";

                return (UiControl?)Controls.Stack
                    .WithStyle("max-width: 640px; margin: 24px auto; gap: 16px;")
                    .WithView(Controls.Markdown($"## {title}\n\nAPI key: **{keyState}**"))
                    // Editable endpoint URL — bound DIRECTLY to the node stream (auto-persists, no
                    // /data replica). Only the single `endpoint` field; the secret keys are never declared.
                    .WithView(new MeshNodeContentEditorControl(path)
                    {
                        Fields = ImmutableList.Create(
                            new MeshNodeEditorField("endpoint", "URL", MeshNodeEditorFieldKind.Text))
                    })
                    .WithView(Controls.Button("Enter Key")
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction((Action<UiActionContext>)(ctx => ShowKeyDialog(ctx, path))));
            });

    private static void ShowKeyDialog(UiActionContext ctx, string providerPath)
    {
        var formId = $"providerKey_{Guid.NewGuid():N}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["key"] = "" });

        var form = Controls.Stack.WithStyle("gap: 12px; padding: 16px;")
            .WithView(Controls.Markdown("Paste the API key. It is stored encrypted and never displayed."))
            .WithView(new TextFieldControl(new JsonPointerReference("key"))
            {
                Label = "API key",
                Placeholder = "paste key here",
                Password = true,
                DataContext = LayoutAreaReference.GetDataPointer(formId),
            });

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(cancel =>
                    cancel.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button("Save key")
                .WithAppearance(Appearance.Accent)
                .WithClickAction((Action<UiActionContext>)(save =>
                    save.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(values =>
                        {
                            var newKey = values?.GetValueOrDefault("key")?.ToString()?.Trim();
                            save.Host.UpdateArea(DialogControl.DialogArea, null!);
                            if (!string.IsNullOrEmpty(newKey))
                                SaveKey(save, providerPath, newKey);
                        }))));

        var dialog = Controls.Dialog(form, "Enter API key").WithSize("S").WithActions(actions);
        ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
    }

    // Inline equivalent of ModelProviderService.RotateKey (Memex.Portal.Shared — not referenceable here):
    // encrypt-at-rest via IProviderKeyProtector, own-node stream.Update (never reads the old key), then a
    // force-persist SaveMeshNodeRequest (sync-driven nodes don't always fire the per-node saveSub). Runs
    // under the caller's identity so RLS gates the write — NO ImpersonateAsSystem.
    private static void SaveKey(UiActionContext ctx, string providerPath, string newKey)
    {
        var hub = ctx.Host.Hub;
        var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
        var stored = protector is null ? newKey : protector.Protect(newKey);

        ctx.Host.Workspace.GetMeshNodeStream(providerPath)
            .Update(current =>
                current.Content is ModelProviderConfiguration cfg
                    ? current with { Content = cfg with { ApiKey = stored } }
                    : current)
            .Subscribe(
                updated => hub.Post(new SaveMeshNodeRequest(updated), o => o.WithTarget(new Address(providerPath))),
                ex => hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.AI.ModelProviderLayoutAreas")
                    ?.LogWarning(ex, "Saving provider key failed for {Path}", providerPath));
    }
}

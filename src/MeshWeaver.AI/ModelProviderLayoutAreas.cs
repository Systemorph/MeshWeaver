using MeshWeaver.Mesh.Security;
using System.Collections.Immutable;
using System.Linq;
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

    /// <summary>
    /// Registers the minimal <c>ModelProvider</c> detail layout, wiring both the default
    /// <c>Overview</c> area and the <c>/Edit</c> route to the same secret-safe
    /// <see cref="Overview"/> view (replacing the generic node editor that leaked the API key).
    /// </summary>
    /// <param name="configuration">The hub configuration to extend with the provider views.</param>
    /// <returns>The same <paramref name="configuration"/> for fluent chaining.</returns>
    public static MessageHubConfiguration AddModelProviderViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(OverviewArea)
            .WithView(OverviewArea, Overview)
            // The /Edit route renders the SAME minimal form — the generic EditNode editor is what
            // "did not work at all" (and leaked the key); point it at our view too.
            .WithView(MeshNodeLayoutAreas.EditArea, Overview));

    /// <summary>
    /// Renders the provider detail: title, a read-only "key set/not set" indicator, an editable
    /// endpoint field bound directly to the node stream, a write-only "Enter Key" button, and the
    /// provider's configured models. Never displays the secret key. Bound live to the node stream.
    /// </summary>
    /// <param name="host">The layout-area host providing the workspace and node stream.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>An observable that emits the provider detail control on every node-stream change.</returns>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
        => host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node is null)
                    return (UiControl?)Controls.Markdown(host.Localize("ui.mdNoProviderData"));

                // A freshly-created provider has no config yet — the create flow persists an Active
                // node with null Content and edits it here (Overview doubles as the Edit area). Show a
                // default config so the endpoint + Enter Key controls render; writes persist it.
                var cfg = node.ContentAs<ModelProviderConfiguration>(host.Hub.JsonSerializerOptions)
                          ?? new ModelProviderConfiguration { Provider = node.Name ?? node.Id };

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
                    .WithView(Controls.Button(host.Localize("ui.enterKey"))
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction((Action<UiActionContext>)(ctx => ShowKeyDialog(ctx, path))))
                    // The models this provider exposes (its LanguageModel children) — surfaced on the
                    // provider page so the catalog is visible, not just the key/endpoint controls.
                    .WithView(ModelsView(cfg));
            });

    // The provider's models, from the denormalized snapshot on the ModelProvider node (the same ids
    // its LanguageModel children carry). Markdown list — no hand-built HTML.
    private static UiControl ModelsView(ModelProviderConfiguration cfg)
    {
        var body = cfg.Models.IsDefaultOrEmpty
            ? "_No models configured._"
            : string.Join("\n", cfg.Models.Select(m => $"- `{m}`"));
        return Controls.Markdown($"### Models\n\n{body}");
    }

    private static void ShowKeyDialog(UiActionContext ctx, string providerPath)
    {
        var formId = $"providerKey_{Guid.NewGuid():N}";
        ctx.Host.UpdateData(formId, new Dictionary<string, object?> { ["key"] = "" });

        var form = Controls.Stack.WithStyle("gap: 12px; padding: 16px;")
            .WithView(Controls.Markdown(ctx.Host.Localize("ui.pasteApiKey")))
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
            .WithView(Controls.Button(ctx.Host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction((Action<UiActionContext>)(cancel =>
                    cancel.Host.UpdateArea(DialogControl.DialogArea, null!))))
            .WithView(Controls.Button(ctx.Host.Localize("ui.saveKey"))
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

        // 🚨 Protect INSIDE the chain, via Defer. Protect REFUSES (throws) when the deployment has
        // no master key configured — it no longer degrades to a plaintext passthrough, which is
        // what used to put a raw key into node content. Computing `stored` before the chain would
        // let that refusal escape from a Subscribe's OnNext with nowhere to go; deferred, it
        // becomes an OnError on the same subscription the write already reports through, so the
        // key is not stored AND the reason is logged. GetRequiredService, not GetService: the
        // protector is registered unconditionally by AddGraph, so a null one is a broken host, not
        // a licence to store the key in the clear.
        Observable.Defer(() => Observable.Return(
                hub.ServiceProvider.GetRequiredService<IProviderKeyProtector>().Protect(newKey)))
            .SelectMany(stored => ctx.Host.Workspace.GetMeshNodeStream(providerPath)
                .Update(current =>
                    // ContentAs, not `is`: an `is ModelProviderConfiguration` returns the node
                    // UNCHANGED whenever the content arrived as a degraded JsonElement — a save
                    // that silently does nothing, on the one control whose entire job is to store
                    // the key. ContentAs recovers that shape.
                    current.ContentAs<ModelProviderConfiguration>(hub.JsonSerializerOptions) is { } cfg
                        ? current with { Content = cfg with { ApiKey = stored } }
                        : current))
            .Subscribe(
                updated => hub.Post(new SaveMeshNodeRequest(updated), o => o.WithTarget(new Address(providerPath))),
                ex => hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.AI.ModelProviderLayoutAreas")
                    ?.LogError(ex, "Saving provider key failed for {Path} — the key was NOT stored", providerPath));
    }
}

using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for the public privacy statement (<c>Admin/Privacy</c>, served at
/// <c>/privacy</c>). Gated to platform admins (<see cref="AdminMenuGate.IsPlatformAdmin"/>).
/// Edits the statement through the STANDARD node-bound markdown editor
/// (<see cref="MarkdownEditorControl.WithAutoSave"/> — the same path the Markdown node's own
/// editor uses; the node's content IS a <c>MarkdownContent</c>, so the whole-content auto-save is
/// the correct shape here). The node is created on first open, prefilled with the generic EU/CH
/// default statement (<see cref="PrivacyStatementNode.DefaultStatement"/>).
/// </summary>
public static class PrivacySettingsTab
{
    public const string TabId = "Privacy";

    // The menu entry is a seeded UiContribution node with Gates.AdminOnly
    // (PlatformSettingsTabAreas); the SettingsPrivacy layout area re-asserts the admin gate.

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2(host.Localize("auth.privacyStatement")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "This statement is shown publicly at [/privacy](/privacy) — no login required — and is " +
            "what external app registrations (e.g. LinkedIn) link as the privacy policy URL. It " +
            "starts from a generic statement drafted for EU (GDPR) and Swiss (revFADP) law; edit it " +
            "below to match your deployment. Changes are saved automatically."));

        // Editor bound DIRECTLY to Admin/Privacy (the reference shape, MarkdownEditLayoutArea): its
        // Value is a POINTER into the node's MarkdownContent, resolved and kept live on the GUI side
        // through IMeshNodeStreamCache — never the statement read once on this hub and baked in.
        // The one thing the slot waits for is EnsureExists (create-on-absent as System, prefilled
        // with the default statement): a write PRECONDITION, so the editor never binds to a node
        // that is not there yet (a point read of an absent node trips the storm breaker). It reads
        // no value of the node.
        stack = stack.WithView((h, _) => PrivacyStatementNode
            .EnsureExists(h.Hub, h.Hub.ServiceProvider.GetService<AccessService>())
            .Select(_ => (UiControl?)BuildEditor(h.Hub.Address.ToString(), h.ViewerLocale()))
            .Catch<UiControl?, Exception>(ex =>
                Observable.Return((UiControl?)Controls.Markdown(host.Localize("privacy.loadFailed", ex.Message))))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingPrivacy"))));

        return stack;
    }

    /// <summary>
    /// The privacy-statement editor, bound by pointer to <c>Admin/Privacy</c>'s markdown and
    /// auto-saving through the node stream. Reads nothing.
    /// </summary>
    internal static MarkdownEditorControl BuildEditor(string hubAddress, string? locale)
        => new MarkdownEditorControl
            {
                Value = new JsonPointerReference(MarkdownEditLayoutArea.MarkdownBodyPointer),
                DataContext = LayoutAreaReference.GetMeshNodeDataContext(PrivacyStatementNode.NodePath),
            }
            .WithDocumentId(PrivacyStatementNode.NodePath)
            .WithHeight("calc(100vh - 320px)")
            .WithMaxHeight("none")
            .WithPlaceholder(LocalizationCatalog.Get("privacy.placeholder", locale))
            .WithAutoSave(hubAddress, PrivacyStatementNode.NodePath);
}

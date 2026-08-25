using System;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Blazor.Views;
using MeshWeaver.Blazor.Graph;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// 🚨 The view-pack REGISTRATION gate: every pack entry point actually wires its controls to its
/// views, all the way through <see cref="LayoutClientConfiguration"/> — the resolution the real
/// renderer uses.
///
/// <para><b>Why this exists.</b> A view pack whose registration extension silently stops mapping a
/// control does not fail anywhere: the control renders through the escaped-HTML fallback slot, the
/// page looks "empty", and nothing goes red. That is exactly how the AppleMaps and OpenStreetMap
/// packs once shipped permanently inert — their registration was never exercised by any test. The
/// previous incarnation of this gate (<c>ViewPackModuleTest</c>, deleted in 11454517a when six
/// packs moved to MeshWeaver.Plugins) asserted only the module-attribute SHAPE; this one goes
/// further and drives each pack's registration through a real hub, then resolves each control the
/// pack claims and asserts the descriptor targets the pack's view type.</para>
///
/// <para><b>Why the client hub deliberately does NOT call <c>AddBlazor()</c>.</b> The base
/// registry's default mapping covers many controls itself; with it in the chain, a pack entry that
/// is ALSO covered by a base-registry arm would keep resolving even if the pack's registration
/// were deleted — the gate would be vacuous for precisely the entries most at risk during an
/// extraction. With only <c>AddLayoutClient()</c> (the host seam that owns
/// <see cref="ILayoutClient"/>) plus the pack extensions, every green assertion is evidence about
/// the PACK's registration and nothing else, and there is no fallback slot to mask a decline:
/// an unregistered control resolves to null and the assertion names it.</para>
///
/// <para><b>Non-vacuity.</b> Verified by falsification at introduction time: commenting out a
/// single <c>WithView&lt;,&gt;()</c> line in <c>AddGraphViews</c> makes exactly the corresponding
/// assertion fail with a null descriptor. When a pack gains a control view, add the pair here —
/// a pack registration without a line in this gate is exactly the hole this test closes.</para>
/// </summary>
public class ViewPackRegistrationGateTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration
            // The host seam alone: ILayoutClient + the view-configuration fold. Deliberately no
            // AddBlazor() — see the class remarks; the base registry must not be able to answer
            // for a pack whose own registration went missing.
            .AddLayoutClient()
            .AddGraphViews()
            .AddChatViews()
            .AddUserProfileViews()
            // EntityViews' gate rows moved WITH its source to MeshWeaver.Plugins
            // (src/MeshWeaver.Blazor.EntityViews.Test — MeshWeaver#2169): a by-name assertion
            // needs a compiled reference, which only the pack's new home carries.
            .AddDefaultViews();

    /// <summary>
    /// Resolves <paramref name="control"/> through the client's real
    /// <see cref="LayoutClientConfiguration"/> and asserts the descriptor targets
    /// <paramref name="expectedView"/>.
    /// </summary>
    private static void AssertPackResolves(IMessageHub client, UiControl control, Type expectedView, string packEntryPoint)
    {
        var layoutClient = client.ServiceProvider.GetRequiredService<ILayoutClient>();

        var descriptor = layoutClient.GetViewDescriptor(control, null, "gate");

        descriptor.Should().NotBeNull(
            because: $"{packEntryPoint} claims to register a view for {control.GetType().Name}; a null "
                   + "descriptor means the pack's registration is inert and the control would render "
                   + "as escaped HTML in production — the AppleMaps/OpenStreetMap failure shape");
        descriptor!.Type.Should().Be(expectedView,
            because: $"{packEntryPoint} maps {control.GetType().Name} to {expectedView.Name}");
    }

    /// <summary>
    /// <c>AddGraphViews</c> (MeshWeaver.Blazor.Graph) — every control the pack registers resolves
    /// to the pack's view.
    /// </summary>
    [Theory]
    [InlineData(typeof(MeshNodeEditorControl), typeof(MeshNodeEditorView))]
    [InlineData(typeof(MeshNodeThumbnailControl), typeof(MeshNodeThumbnailView))]
    [InlineData(typeof(MeshNodeCardControl), typeof(MeshNodeCardView))]
    [InlineData(typeof(MeshNodeCollectionControl), typeof(MeshNodeCollectionView))]
    [InlineData(typeof(MeshNodeContentEditorControl), typeof(MeshNodeContentEditorView))]
    [InlineData(typeof(MeshNodeRoleEditorControl), typeof(MeshNodeRoleEditorView))]
    [InlineData(typeof(MeshNodePickerControl), typeof(MeshNodePickerView))]
    public void GraphPack_RegistersItsViews(Type controlType, Type viewType)
    {
        var client = GetClient();
        AssertPackResolves(client, CreateControl(client, controlType), viewType, "AddGraphViews");
    }

    /// <summary>
    /// <c>AddDefaultViews</c> (MeshWeaver.Blazor.Views) — the DEFAULT control set resolves to the
    /// pack's views. These arms lived in the base registry until the pack was factored out; a
    /// regression here means the standard portal renders raw control JSON through the fallback.
    /// </summary>
    [Theory]
    [InlineData(typeof(ButtonControl), typeof(ButtonView))]
    [InlineData(typeof(BadgeControl), typeof(BadgeView))]
    [InlineData(typeof(IconControl), typeof(IconView))]
    [InlineData(typeof(MenuItemControl), typeof(MenuItemView))]
    [InlineData(typeof(MarkdownControl), typeof(MeshWeaver.Blazor.Components.MarkdownView))]
    [InlineData(typeof(MarkdownEditorControl), typeof(MarkdownEditorView))]
    [InlineData(typeof(CodeEditorControl), typeof(CodeEditorView))]
    [InlineData(typeof(ProgressControl), typeof(ProgressView))]
    [InlineData(typeof(SpacerControl), typeof(SpacerView))]
    [InlineData(typeof(RedirectControl), typeof(RedirectView))]
    [InlineData(typeof(VideoControl), typeof(VideoView))]
    [InlineData(typeof(SearchBoxControl), typeof(SearchBoxView))]
    [InlineData(typeof(MeshSearchControl), typeof(MeshSearchView))]
    [InlineData(typeof(HighlightControl), typeof(HighlightView))]
    [InlineData(typeof(AppearanceControl), typeof(AppearanceView))]
    public void DefaultViewsPack_RegistersItsControlViews(Type controlType, Type viewType)
    {
        var client = GetClient();
        AssertPackResolves(client, CreateControl(client, controlType), viewType, "AddDefaultViews");
    }

    /// <summary>
    /// <c>AddDefaultViews</c> — the standard skins resolve to the pack's skinned views.
    /// </summary>
    [Theory]
    [InlineData(typeof(LayoutSkin), typeof(LayoutView))]
    [InlineData(typeof(LayoutGridSkin), typeof(LayoutGridView))]
    [InlineData(typeof(NavMenuSkin), typeof(NavMenuView))]
    [InlineData(typeof(MainSkin), typeof(MainView))]
    [InlineData(typeof(ToolbarSkin), typeof(ToolbarView))]
    [InlineData(typeof(LayoutStackSkin), typeof(LayoutStackView))]
    [InlineData(typeof(HeaderSkin), typeof(HeaderView))]
    [InlineData(typeof(CardSkin), typeof(CardView))]
    [InlineData(typeof(FooterSkin), typeof(FooterView))]
    [InlineData(typeof(TabsSkin), typeof(TabsView))]
    public void DefaultViewsPack_RegistersItsSkinViews(Type skinType, Type viewType)
    {
        var skinned = new HtmlControl("<p>x</p>").AddSkin((Skin)Activator.CreateInstance(skinType)!);
        AssertPackResolves(GetClient(), skinned, viewType, "AddDefaultViews");
    }

    /// <summary>
    /// <c>AddChatViews</c> (MeshWeaver.Blazor.Portal) — the thread-chat control resolves to the
    /// pack's view.
    /// </summary>
    [Fact]
    public void ChatPack_RegistersItsViews()
        => AssertPackResolves(GetClient(), new ThreadChatControl(), typeof(ThreadChatView), "AddChatViews");

    /// <summary>
    /// <c>AddUserProfileViews</c> (MeshWeaver.Blazor.Portal) — the user-profile control resolves
    /// to the pack's view.
    /// </summary>
    [Fact]
    public void UserProfilePack_RegistersItsViews()
        => AssertPackResolves(GetClient(), new UserProfileControl(), typeof(UserProfilePageView), "AddUserProfileViews");

    /// <summary>
    /// Guards the gate itself: a control NO pack registers resolves to null on this hub (there is
    /// no fallback slot here), which is what gives the positive assertions their teeth — a decline
    /// is observable, not masked.
    /// </summary>
    /// <summary>A control shape no pack has ever seen — the gate's negative probe. HtmlControl
    /// stopped qualifying when the default-views pack (which owns it) joined this hub.</summary>
    private sealed record UnownedProbeControl() : UiControl<UnownedProbeControl>("probe", "1.0.0");

    [Fact]
    public void UnregisteredControl_ResolvesToNull()
    {
        var layoutClient = GetClient().ServiceProvider.GetRequiredService<ILayoutClient>();

        var descriptor = layoutClient.GetViewDescriptor(new UnownedProbeControl(), null, "gate");

        descriptor.Should().BeNull(
            because: "no pack on this hub maps UnownedProbeControl and no fallback is registered — if this "
                   + "resolves, something answers for controls it does not own and the positive "
                   + "assertions above stop being evidence about the packs");
    }

    /// <summary>
    /// Instantiates a control record for the theory rows: the constructors differ only in the
    /// required path/title/options arguments they take, and the values are irrelevant to view
    /// resolution (matching is by TYPE) — EXCEPT the two reflection-closed generics, whose
    /// <c>Type</c> field must name a registry type so the pack's map can close
    /// <c>NumberFieldView&lt;T&gt;</c> / <c>RadioGroupView&lt;T&gt;</c>, exactly as the editor
    /// builders produce it in production (<c>typeRegistry.GetOrAddType(propertyType)</c>).
    /// </summary>
    private static UiControl CreateControl(IMessageHub client, Type controlType)
    {
        var typeRegistry = client.ServiceProvider.GetRequiredService<MeshWeaver.Domain.ITypeRegistry>();
        return controlType switch
        {
            _ when controlType == typeof(MeshNodeEditorControl) => new MeshNodeEditorControl(),
            _ when controlType == typeof(MeshNodeThumbnailControl) => new MeshNodeThumbnailControl("path", "title"),
            _ when controlType == typeof(MeshNodeCardControl) => new MeshNodeCardControl("path"),
            _ when controlType == typeof(MeshNodeContentEditorControl) => new MeshNodeContentEditorControl("path"),
            _ when controlType == typeof(MeshNodeRoleEditorControl) => new MeshNodeRoleEditorControl("path", 0),
            _ when controlType == typeof(MeshNodeCollectionControl) => new MeshNodeCollectionControl(),
            _ when controlType == typeof(MeshNodePickerControl) => new MeshNodePickerControl("/data"),
            _ when controlType == typeof(ButtonControl) => new ButtonControl("go"),
            _ when controlType == typeof(BadgeControl) => new BadgeControl("b"),
            _ when controlType == typeof(IconControl) => new IconControl("icon"),
            _ when controlType == typeof(MenuItemControl) => new MenuItemControl("t", "i"),
            _ when controlType == typeof(MarkdownControl) => new MarkdownControl("# m"),
            _ when controlType == typeof(MarkdownEditorControl) => new MarkdownEditorControl(),
            _ when controlType == typeof(CodeEditorControl) => new CodeEditorControl(),
            _ when controlType == typeof(ProgressControl) => new ProgressControl("m", 1),
            _ when controlType == typeof(SpacerControl) => new SpacerControl(),
            _ when controlType == typeof(RedirectControl) => new RedirectControl("/x"),
            _ when controlType == typeof(VideoControl) => new VideoControl("https://cdn/x.mp4"),
            _ when controlType == typeof(SearchBoxControl) => new SearchBoxControl(),
            _ when controlType == typeof(MeshSearchControl) => new MeshSearchControl(),
            _ when controlType == typeof(HighlightControl) => new HighlightControl("t"),
            _ when controlType == typeof(AppearanceControl) => new AppearanceControl(),
            _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, "add the control here when the gate gains a row")
        };
    }
}

using System;
using MeshWeaver.Blazor.EntityViews;
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
            .AddEntityViews();

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
    /// <c>AddEntityViews</c> (MeshWeaver.Blazor.EntityViews) — every form control the pack
    /// registers resolves to the pack's view, including the two reflection-closed generics
    /// (<c>NumberFieldView&lt;T&gt;</c> / <c>RadioGroupView&lt;T&gt;</c>, closed on the control's
    /// declared value type through the hub's type registry).
    /// </summary>
    [Theory]
    [InlineData(typeof(TextFieldControl), typeof(TextFieldView))]
    [InlineData(typeof(TextAreaControl), typeof(TextAreaView))]
    [InlineData(typeof(DateTimeControl), typeof(DateTimeView))]
    [InlineData(typeof(ComboboxControl), typeof(Combobox))]
    [InlineData(typeof(ListboxControl), typeof(Listbox))]
    [InlineData(typeof(SelectControl), typeof(SelectView))]
    [InlineData(typeof(CheckBoxControl), typeof(Checkbox))]
    [InlineData(typeof(SwitchControl), typeof(Switch))]
    [InlineData(typeof(NumberFieldControl), typeof(NumberFieldView<int>))]
    [InlineData(typeof(RadioGroupControl), typeof(RadioGroupView<string>))]
    public void EntityViewsPack_RegistersItsControlViews(Type controlType, Type viewType)
    {
        var client = GetClient();
        AssertPackResolves(client, CreateControl(client, controlType), viewType, "AddEntityViews");
    }

    /// <summary>
    /// <c>AddEntityViews</c> — the three entity-editing SKINS resolve to the pack's skinned
    /// views. The skin rides the control's skin stack, exactly as the renderer sees it; the
    /// pack's map pops it and returns the skin view.
    /// </summary>
    [Theory]
    [InlineData(typeof(EditorSkin), typeof(EditorView))]
    [InlineData(typeof(EditFormSkin), typeof(EditFormView))]
    [InlineData(typeof(PropertySkin), typeof(PropertyView))]
    public void EntityViewsPack_RegistersItsSkinViews(Type skinType, Type viewType)
    {
        var skinned = new HtmlControl("<p>x</p>").AddSkin((Skin)Activator.CreateInstance(skinType)!);
        AssertPackResolves(GetClient(), skinned, viewType, "AddEntityViews");
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
    [Fact]
    public void UnregisteredControl_ResolvesToNull()
    {
        var layoutClient = GetClient().ServiceProvider.GetRequiredService<ILayoutClient>();

        var descriptor = layoutClient.GetViewDescriptor(new HtmlControl("<p>x</p>"), null, "gate");

        descriptor.Should().BeNull(
            because: "no pack on this hub maps HtmlControl and no fallback is registered — if this "
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
            _ when controlType == typeof(TextFieldControl) => new TextFieldControl("/data"),
            _ when controlType == typeof(TextAreaControl) => new TextAreaControl("/data"),
            _ when controlType == typeof(DateTimeControl) => new DateTimeControl("/data"),
            _ when controlType == typeof(ComboboxControl) => new ComboboxControl("/data", Array.Empty<object>()),
            _ when controlType == typeof(ListboxControl) => new ListboxControl("/data", Array.Empty<object>()),
            _ when controlType == typeof(SelectControl) => new SelectControl("/data", Array.Empty<object>()),
            _ when controlType == typeof(CheckBoxControl) => new CheckBoxControl("/data"),
            _ when controlType == typeof(SwitchControl) => new SwitchControl("/data"),
            _ when controlType == typeof(NumberFieldControl) => new NumberFieldControl("/data", typeRegistry.GetOrAddType(typeof(int))),
            _ when controlType == typeof(RadioGroupControl) => new RadioGroupControl("/data", Array.Empty<object>(), typeRegistry.GetOrAddType(typeof(string))),
            _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, "add the control here when the gate gains a row")
        };
    }
}

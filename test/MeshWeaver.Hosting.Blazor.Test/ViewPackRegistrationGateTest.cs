using System;
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
            .AddUserProfileViews();

    /// <summary>
    /// Resolves <paramref name="control"/> through the client's real
    /// <see cref="LayoutClientConfiguration"/> and asserts the descriptor targets
    /// <paramref name="expectedView"/>.
    /// </summary>
    private void AssertPackResolves(UiControl control, Type expectedView, string packEntryPoint)
    {
        var layoutClient = GetClient().ServiceProvider.GetRequiredService<ILayoutClient>();

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
    [InlineData(typeof(MeshNodeThumbnailControl), typeof(MeshWeaver.Blazor.Components.MeshNodeThumbnailView))]
    [InlineData(typeof(MeshNodeCardControl), typeof(MeshWeaver.Blazor.Components.MeshNodeCardView))]
    [InlineData(typeof(MeshNodeContentEditorControl), typeof(MeshWeaver.Blazor.Components.MeshNodeContentEditorView))]
    [InlineData(typeof(MeshNodeRoleEditorControl), typeof(MeshWeaver.Blazor.Components.MeshNodeRoleEditorView))]
    public void GraphPack_RegistersItsViews(Type controlType, Type viewType)
        => AssertPackResolves(CreateControl(controlType), viewType, "AddGraphViews");

    /// <summary>
    /// <c>AddChatViews</c> (MeshWeaver.Blazor.Portal) — the thread-chat control resolves to the
    /// pack's view.
    /// </summary>
    [Fact]
    public void ChatPack_RegistersItsViews()
        => AssertPackResolves(new ThreadChatControl(), typeof(ThreadChatView), "AddChatViews");

    /// <summary>
    /// <c>AddUserProfileViews</c> (MeshWeaver.Blazor.Portal) — the user-profile control resolves
    /// to the pack's view.
    /// </summary>
    [Fact]
    public void UserProfilePack_RegistersItsViews()
        => AssertPackResolves(new UserProfileControl(), typeof(UserProfilePageView), "AddUserProfileViews");

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
    /// Instantiates a control record for the theory rows: the constructors differ only in how many
    /// required path/title strings they take, and the values are irrelevant to view resolution
    /// (matching is by TYPE).
    /// </summary>
    private static UiControl CreateControl(Type controlType) =>
        controlType switch
        {
            _ when controlType == typeof(MeshNodeEditorControl) => new MeshNodeEditorControl(),
            _ when controlType == typeof(MeshNodeThumbnailControl) => new MeshNodeThumbnailControl("path", "title"),
            _ when controlType == typeof(MeshNodeCardControl) => new MeshNodeCardControl("path"),
            _ when controlType == typeof(MeshNodeContentEditorControl) => new MeshNodeContentEditorControl("path"),
            _ when controlType == typeof(MeshNodeRoleEditorControl) => new MeshNodeRoleEditorControl("path", 0),
            _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, "add the control here when the gate gains a row")
        };
}

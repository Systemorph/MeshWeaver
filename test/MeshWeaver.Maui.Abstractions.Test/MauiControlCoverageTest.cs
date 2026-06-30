using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// The MAUI parity ratchet (Tier 1 half — runs in normal CI without the maccatalyst toolchain).
/// Reflects over EVERY concrete framework <c>UiControl</c> and asserts it is accounted for in the
/// <see cref="MauiControlManifest"/>: supported (explicit native view), planned (remaining work), or a
/// container (rendered by the pack's generic ContainerView). Adding a new framework control fails this
/// test until it is classified — preventing silent parity gaps. The complementary Tier-3 device test
/// (runs once the toolchain is fixed) asserts the view pack's BuildRegistry actually registers exactly
/// <see cref="MauiControlManifest.SupportedLeafControls"/>.
/// </summary>
public class MauiControlCoverageTest
{
    // Anchors force-load the assemblies that define UiControl subclasses (Layout / Graph / AI).
    private static readonly Type[] Anchors = [typeof(LabelControl), typeof(MeshNodeContentEditorControl), typeof(ThreadViewModel)];

    private static List<Type> AllConcreteControls() => Anchors
        .Select(a => a.Assembly).Distinct()
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsClass: true, IsGenericTypeDefinition: false }
                    && typeof(UiControl).IsAssignableFrom(t))
        .ToList();

    [Fact]
    public void ManifestNamesAreAllRealControls()
    {
        var all = AllConcreteControls().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var stale = MauiControlManifest.SupportedLeafControls
            .Concat(MauiControlManifest.PlannedControls)
            .Where(n => !all.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal);
        // Empty string ⇒ no stale entries; otherwise the failure lists them.
        string.Join(", ", stale).Should().BeEmpty();
    }

    [Fact]
    public void SupportedAndPlannedAreDisjoint()
    {
        var overlap = MauiControlManifest.SupportedLeafControls
            .Intersect(MauiControlManifest.PlannedControls)
            .OrderBy(n => n, StringComparer.Ordinal);
        string.Join(", ", overlap).Should().BeEmpty();
    }

    [Fact]
    public void EveryConcreteControlIsSupportedPlannedOrContainer()
    {
        var unclassified = AllConcreteControls()
            .Where(t => !typeof(IContainerControl).IsAssignableFrom(t))
            .Where(t => !MauiControlManifest.SupportedLeafControls.Contains(t.Name))
            .Where(t => !MauiControlManifest.PlannedControls.Contains(t.Name))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal);
        // Empty ⇒ full parity coverage of the control surface; otherwise lists the unclassified controls.
        string.Join(", ", unclassified).Should().BeEmpty();
    }

    [Fact]
    public void ChatAndEditorAreSupported()
    {
        MauiControlManifest.SupportedLeafControls.Should().Contain("ThreadChatControl");
        MauiControlManifest.SupportedLeafControls.Should().Contain("ThreadMessageBubbleControl");
        MauiControlManifest.SupportedLeafControls.Should().Contain("MeshNodeContentEditorControl");
    }
}

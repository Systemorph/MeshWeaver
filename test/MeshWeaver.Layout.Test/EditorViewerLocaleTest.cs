using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// MeshWeaver#7383: the <c>host.Edit</c> form macro must describe its fields in the language of the
/// viewer the layout area renders for — also when the area builds its form AFTER an async gate,
/// where the ambient AccessContext is no longer the viewer's.
///
/// <para>The gate here is a subject the TEST completes from its own thread, with the German viewer's
/// scope already disposed: exactly the shape of a permission or data gate whose answer arrives on a
/// thread that never carried the viewer. The macro used to read the language from that ambient
/// context, so the descriptions came out English; it now reads the viewer the host captured.</para>
/// </summary>
public class EditorViewerLocaleTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A form whose one field carries a German translation of its description.</summary>
    public record Localized
    {
        /// <summary>The field under test.</summary>
        [Description("Display time zone")]
        [Translation("de", "Anzeige-Zeitzone")]
        public string? Zone { get; init; }
    }

    private static readonly AccessContext GermanViewer = new() { ObjectId = "viewer", Name = "Viewer", Locale = "de" };

    private readonly Subject<Unit> gate = new();

    private UiControl Immediate(LayoutAreaHost host, RenderingContext _) => host.Edit(new Localized(), "immediate");

    private IObservable<UiControl> Gated(LayoutAreaHost host, RenderingContext _)
        => gate.Take(1).Select(_ => host.Edit(new Localized(), "gated"));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(layout => layout
            .WithView(nameof(Immediate), Immediate)
            .WithView(nameof(Gated), Gated));

    private ISynchronizationStream<EntityStore> OpenAsGermanViewer(string area)
    {
        var host = GetHost();
        var access = host.ServiceProvider.GetRequiredService<AccessService>();
        using (access.SwitchAccessContext(GermanViewer))
            return host.GetWorkspace().GetStream(new LayoutAreaReference(area))!;
    }

    private static async Task<string?> FieldDescription(ISynchronizationStream<EntityStore> stream, string area)
    {
        var control = await stream.GetControlStream(area)
            .Should().Within(10.Seconds()).Match(x => x is EditorControl);
        var editor = control.Should().BeOfType<EditorControl>().Subject;
        return editor.Areas.Should().ContainSingle().Subject
            .Skins.OfType<PropertySkin>().Should().ContainSingle().Subject
            .Description as string;
    }

    /// <summary>
    /// The control: a form built on the render turn, where the restored ambient context IS the
    /// viewer's, is German. It proves the fixture really renders for a German viewer, so the gated
    /// case below fails for the gate and not for the setup.
    /// </summary>
    [Fact]
    public async Task AFormBuiltOnTheRenderTurn_IsInTheViewersLanguage()
    {
        var stream = OpenAsGermanViewer(nameof(Immediate));
        (await FieldDescription(stream, nameof(Immediate))).Should().Be("Anzeige-Zeitzone");
    }

    /// <summary>
    /// The defect: the same form built after an async gate, on a thread that never carried the
    /// viewer. Before the fix the description was "Display time zone".
    /// </summary>
    [Fact]
    public async Task AFormBuiltAfterAnAsyncGate_IsStillInTheViewersLanguage()
    {
        var stream = OpenAsGermanViewer(nameof(Gated));
        using var opened = stream.Subscribe(_ => { });
        // The render must be waiting on the gate before the gate opens — a plain subject drops an
        // emission nobody listens to, and a replaying one would hand it to the render turn instead.
        SpinWait.SpinUntil(() => gate.HasObservers, TimeSpan.FromSeconds(10)).Should()
            .BeTrue("the gated area subscribes to its gate when it renders");

        // Released from the test thread, outside the viewer's scope: the ambient context here is
        // not the German viewer, exactly as on a thread that delivers a permission answer.
        gate.OnNext(Unit.Default);

        (await FieldDescription(stream, nameof(Gated))).Should().Be("Anzeige-Zeitzone",
            "the form describes its fields in the language of the viewer the area renders for, "
            + "not in whatever the ambient context holds when the gate opens");
    }
}

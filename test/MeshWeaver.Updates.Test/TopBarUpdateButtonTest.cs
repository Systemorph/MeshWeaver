using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// The portal top bar's <b>Update</b> button, end to end on a real mesh and a real N → N+1 compile.
/// The button is the <c>$Menu:Update</c> slot of the page's own area stream
/// (<see cref="StaleBuildBanner.UpdateMenuContext"/>); pressing it runs the node's recycle on the
/// caller's hub (<see cref="HubRecycleExtensions.RecycleNode"/>, the same call the portal shell
/// makes). Three facts, each with its control:
/// <list type="bullet">
///   <item>No button while the instance runs the current build (negative control).</item>
///   <item>The button appears once N+1 is published, while the page keeps serving N.</item>
///   <item>Pressing it re-binds THAT address on N+1 and the button is gone; a sibling instance of
///     the same type that nobody pressed keeps serving N (it was not recycled).</item>
/// </list>
/// </summary>
public class TopBarUpdateButtonTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    private IObservable<IReadOnlyList<NodeMenuItemDefinition>> UpdateMenu(string instancePath)
    {
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(instancePath), new LayoutAreaReference("Overview"))
            ?? throw new InvalidOperationException($"no layout stream for '{instancePath}'");
        // Read the Update slot only once the hub's menu renderer has demonstrably RUN (its Node
        // menu is populated). An absent slot also reads as empty, so without this the negative
        // halves below would pass on a page whose menus were never rendered at all.
        return stream.GetMenu(NodeMenuItemsExtensions.NodeMenuContext)
            .Where(items => items.Count > 0)
            .Take(1)
            .SelectMany(_ => stream.GetMenu(StaleBuildBanner.UpdateMenuContext));
    }

    [Fact(Timeout = 240_000)]
    public async Task TheButtonShowsIffStale_AndPressingItRebindsOnlyThatAddress()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        var pressed = await CreateInstance(typePath, "pressed");
        var untouched = await CreateInstance(typePath, "untouched");
        await AssertServes(pressed, "N", "the instance activates on build N");
        await AssertServes(untouched, "N", "the sibling activates on build N");

        // ── NEGATIVE CONTROL: current build → the slot holds nothing. ──
        var before = await UpdateMenu(pressed).Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        before.Should().BeEmpty("an instance on the current build offers no Update");

        await EditSource(typePath, Code("N1"));
        var n1 = await Publish(typePath, n);
        n1.CompilationStatus.Should().Be(CompilationStatus.Ok);

        // ── POSITIVE: the published N+1 puts ONE recycle action in the slot (after the settle
        // window), while the live hub keeps serving N. ──
        var offered = await UpdateMenu(pressed)
            .Where(items => items.Count > 0)
            .Take(1)
            .Should().Within(Step + Step).Emit("a published N+1 must offer the Update button",
                cancellationToken: TestContext.Current.CancellationToken);
        var item = offered.Should().ContainSingle().Which;
        item.Action.Should().Be(MenuActions.Recycle);
        item.Href.Should().Be("/" + pressed);
        await AssertServes(pressed, "N", "the offer never recycles by itself (auto-recycle is off here)");

        // ── PRESS: what the portal shell runs for the button. The hub is asked at the press. ──
        var recycled = await Press(pressed);
        recycled.Should().BeTrue("the hub still serves N, so the press recycles it");
        await AssertServes(pressed, "N1", "the activation after the press binds the newest build");
        var after = await UpdateMenu(pressed).Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        after.Should().BeEmpty("the fresh activation is on the newest build, so the button is gone");

        // ── A SECOND press on the now-current hub (a button left on screen) recycles NOTHING: the
        // hub answers "not stale" at the press, so no DisposeRequest is posted. Proven by the
        // activation INSTANCE surviving a round trip that would queue behind any teardown. ──
        var activation = HostedHub(pressed);
        (await Press(pressed)).Should().BeFalse("a current hub is not recycled by a stale button");
        await AssertServes(pressed, "N1", "a read after the press is answered");
        HostedHub(pressed).Should().BeSameAs(activation,
            "the same activation still serves: a press on a current hub must not tear it down");

        // ── EXACTLY that address: the sibling was not recycled and still serves N. ──
        await AssertServes(untouched, "N", "pressing Update on one page must not recycle another");
    }

    private Task<bool> Press(string instancePath)
        => GetClient(c => c.AddData())
            .UpdateToNewestBuild(instancePath, "Updates suite: the top bar's Update was pressed", Step)
            .Should().Within(Step + Step).Emit("the press must be answered",
                cancellationToken: TestContext.Current.CancellationToken);

    private IMessageHub HostedHub(string instancePath)
        => Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>().Hubs
               .FirstOrDefault(h => h.Address.Path == instancePath && !h.IsDisposing)
           ?? throw new InvalidOperationException($"no live activation at '{instancePath}'");
}

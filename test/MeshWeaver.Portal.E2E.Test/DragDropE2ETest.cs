using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// E2E: the generic Draggable / DropTarget controls render in the LIVE Blazor portal and a real HTML5
/// drop round-trips to the hub. Drives the <c>Doc/GUI/DragAndDrop</c> page, whose Roslyn-compiled
/// <c>--render</c> cell produces a <see cref="MeshWeaver.Layout.DraggableControl"/> + a
/// <see cref="MeshWeaver.Layout.DropTargetControl"/>. Proves the control-language → BlazorViewRegistry →
/// DraggableView/DropTargetView path works end to end in a real browser (the server-side DropAction
/// invocation itself is pinned by MeshWeaver.Layout.Test.DragDropTest; the browser drag→DropEvent
/// mechanics by the React / React-Native Playwright suites).
///
/// <para>Gated like the rest of the suite: set <c>E2E_BASE_URL</c> (or <c>E2E_LAUNCH=1</c>) to run;
/// otherwise it Skips.</para>
/// </summary>
[Collection("portal-e2e")]
public class DragDropE2ETest(PortalFixture fixture)
{
    [Fact(Timeout = 300_000)]
    public async Task DragAndDropDoc_RendersDragWiredControls_AndDropRoundTripsCleanly()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        // The page renders a live Draggable/DropTarget via a Roslyn-compiled cell; warm the kernel
        // first so the first-compile cost does not blow this test's budget (fixture contract).
        await fixture.EnsureKernelWarmAsync(context);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync($"{fixture.BaseUrl}/Doc/GUI/DragAndDrop",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });

        // 1. The Blazor DraggableView + DropTargetView rendered (control language → BlazorViewRegistry →
        //    the views), each wrapping its content via the contentArea NamedArea.
        var draggable = page.Locator("[data-draggable]").First;
        var dropTarget = page.Locator("[data-drop-target]").First;
        await draggable.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 240_000 // cold Roslyn compile of the render cell can take a while
        });
        await dropTarget.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        await Assertions.Expect(draggable).ToContainTextAsync("Drag me");
        await Assertions.Expect(dropTarget).ToContainTextAsync("Drop here");
        // The draggable carries its payload for the drag data-transfer.
        await Assertions.Expect(draggable).ToHaveAttributeAsync("data-draggable", "card-1");

        // 2. Drive a real HTML5 drag: the Blazor @ondragstart/@ondrop handlers fire, the DropTargetView
        //    posts a DropEvent to the hub, and LayoutAreaHost.OnDrop processes it. The contract here is
        //    that the drop round-trips WITHOUT wedging the circuit (the primitive has no DropAction, so
        //    there is no visible mutation — the server no-ops cleanly).
        await page.EvalOnSelectorAllAsync("[data-draggable],[data-drop-target]", @"els => {
            const src = els.find(e => e.hasAttribute('data-draggable'));
            const dst = els.find(e => e.hasAttribute('data-drop-target'));
            const dataTransfer = new DataTransfer();
            const fire = (el, type) => el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer }));
            fire(src, 'dragstart');
            fire(dst, 'dragover');
            fire(dst, 'drop');
            fire(src, 'dragend');
        }");

        // 3. The page stays healthy: no emergency error overlay, and the controls survive the drop.
        await Assertions.Expect(page.GetByText("This page can't be displayed")).ToHaveCountAsync(0);
        await Assertions.Expect(draggable).ToContainTextAsync("Drag me");
        await Assertions.Expect(dropTarget).ToContainTextAsync("Drop here");
    }
}

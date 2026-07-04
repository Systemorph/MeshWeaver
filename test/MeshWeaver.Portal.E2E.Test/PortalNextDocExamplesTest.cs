using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The portal-next (React, <c>/next</c>) twin of the doc-example render sweep: every doc page with
/// <c>--render</c> examples (auto-enumerated by <see cref="DocExampleCatalog"/>) must render through
/// the LIVE React shell without the React-side failure markers — the offline fallback, a live-area
/// error, an "Unsupported control" fallback (a control type the Fluent pack doesn't register), a
/// failed interactive kernel, or a raw unresolved <c>@@(</c> macro.
///
/// <para>Content-specific markers stay with <see cref="ReactDocViewsTest"/>; this sweep is the
/// generic gate that keeps EVERY documented example at least mounting in the React GUI.</para>
/// </summary>
[Collection("portal-e2e")]
public class PortalNextDocExamplesTest(PortalFixture fixture)
{
    // Just the page paths — the React sweep asserts the generic render contract (live engages, real
    // content, no failure markers), not a per-example count, so it doesn't carry exampleCount.
    public static TheoryData<string> ExamplePages()
    {
        var data = new TheoryData<string>();
        foreach (var (docPath, count) in DocExampleCatalog.AllPages().OrderBy(p => p.Key, StringComparer.Ordinal))
            if (count > 0)
                data.Add(docPath);
        return data;
    }

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(ExamplePages))]
    public async Task DocPage_RendersInReactShell_WithoutFailureMarkers(string docPath)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        // Pages whose examples need a LANGUAGE WORKER can only execute where it is deployed — the
        // e2e stack ships no python gate, so skip EXPLICITLY (a fail here would blame the GUI).
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("E2E_PYTHON") != "1"
                && DocExampleCatalog.AllPageExamples().TryGetValue(docPath, out var ex)
                && DocExampleCatalog.NeedsLanguageWorker(docPath, ex),
            $"{docPath} needs a language worker (python) — the e2e stack has none (set E2E_PYTHON=1 when it does)");
        // ONE shared authenticated context for the whole sweep (owned by the fixture): a fresh
        // context + /dev/signin per page starved the portal under the sweep's kernel-compile load.
        var context = await fixture.SharedAuthenticatedContextAsync();
        var probe = await context.APIRequest.GetAsync($"{fixture.BaseUrl}/next");
        Assert.SkipUnless((int)probe.Status == 200,
            $"/next not deployed on {fixture.BaseUrl} (HTTP {probe.Status}) — run 'memex-local e2e up'.");
        // Pay the one-time Roslyn warm-up up front, not on whichever page runs first.
        await fixture.EnsureKernelWarmAsync(context);

        var page = await context.NewPageAsync();
        try
        {
        await page.SetViewportSizeAsync(1400, 1000);
        await page.GotoAsync($"{fixture.BaseUrl}/next/{docPath}", new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        // The LIVE stream must take over (data-mw-live flips after the first gRPC-web frame folds).
        await page.Locator("[data-mw-live-area][data-mw-live='true']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 90_000 });

        // The page body renders real content (not a skeleton): some non-trivial text.
        await page.WaitForFunctionAsync("""
            () => {
              const area = document.querySelector('[data-mw-live-area]');
              return area && area.textContent.trim().length > 80;
            }
            """, null, new PageWaitForFunctionOptions { Timeout = 120_000 });

        // Examples execute through the per-view kernel: the "starting" notice must clear and the
        // kernel must not report failure/unavailability (this portal HAS a kernel).
        await page.WaitForFunctionAsync("""
            () => !document.body.textContent.includes('Starting interactive kernel')
            """, null, new PageWaitForFunctionOptions { Timeout = 150_000 });

        Directory.CreateDirectory("/tmp/portal-next-doc-sweep");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"/tmp/portal-next-doc-sweep/{docPath.Replace('/', '-')}.png",
            FullPage = true
        });

        // React-side failure markers — each one is a real defect, not cosmetics.
        (await page.Locator("[data-mw-offline]").CountAsync())
            .Should().Be(0, $"{docPath} must render live, not the offline fallback");
        (await page.Locator("[data-mw-area-error]").CountAsync())
            .Should().Be(0, $"{docPath} must not fail its live area");
        (await page.GetByText("Unsupported control:", new() { Exact = false }).CountAsync())
            .Should().Be(0, $"every control on {docPath} must have a React renderer (no red fallback)");
        (await page.GetByText("Interactive kernel failed to start", new() { Exact = false }).CountAsync())
            .Should().Be(0, $"the interactive kernel must boot for {docPath}");
        (await page.GetByText("Interactive code execution is unavailable", new() { Exact = false }).CountAsync())
            .Should().Be(0, $"the e2e portal has a kernel — {docPath} must not degrade to the unavailable notice");
        // Raw @@ macros OUTSIDE code samples only — doc pages that TEACH the macro syntax
        // (Doc/GUI/MeshSearch) legitimately render "@@(" inside <code>/<pre> spans.
        (await page.EvaluateAsync<int>("""
            () => {
              let count = 0;
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                const n = walker.currentNode;
                if (!n.textContent.includes('@@(')) continue;
                if (n.parentElement?.closest('code, pre, table')) continue;
                count++;
              }
              return count;
            }
            """))
            .Should().Be(0, $"every @@ area macro on {docPath} (outside code samples) must resolve to an embedded region");
        }
        finally
        {
            // Close the page so the react view UNMOUNTS — that releases the per-view kernel
            // (MarkdownKernelSession.dispose → requestedStatus=Cancelled) instead of leaving
            // 50+ live kernels + gRPC streams behind for the rest of the run.
            await page.CloseAsync();
        }
    }
}

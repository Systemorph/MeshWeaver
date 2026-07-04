using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The AUTO-ENUMERATED companion to <see cref="DocExamplesRenderTest"/>: that suite pins deep,
/// page-specific markers for a curated list; this sweep discovers EVERY doc page carrying
/// <c>--render</c> examples from the doc sources themselves (<see cref="DocExampleCatalog"/>) and
/// asserts the generic render contract on the pages the curated list does NOT cover — so a brand-new
/// doc page with examples is browser-verified the moment it is written, with no list to remember.
///
/// <para>Generic contract per page: every example mounts a <c>.layout-area</c>, no example is stuck
/// on the loading ring, and no error placeholder ("Area not found", "No renderer is registered",
/// <c>.layout-area-error</c>, <c>.meshweaver-render-error</c>) renders. Content-specific assertions
/// stay in the curated suite.</para>
/// </summary>
[Collection("portal-e2e")]
public class DocExamplesSweepTest(PortalFixture fixture)
{
    /// <summary>Doc pages with examples that the curated suite does not already cover.</summary>
    public static TheoryData<string, int> SweepPages()
    {
        var curated = CuratedPages().Select(p => p.DocPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var data = new TheoryData<string, int>();
        foreach (var (docPath, count) in DocExampleCatalog.AllPages().OrderBy(p => p.Key, StringComparer.Ordinal))
            if (count > 0 && !curated.Contains(docPath))
                data.Add(docPath, count);
        return data;
    }

    private static IEnumerable<(string DocPath, int ExampleCount)> CuratedPages() =>
        DocExamplesRenderTest.ExamplePages
            .Select(row => ((ITheoryDataRow)row).GetData())
            .Select(data => ((string)data[0]!, (int)data[1]!));

    /// <summary>
    /// The catalog must rediscover every curated page with at least its pinned example count —
    /// this pins the enumeration + fence parser against silently rotting to zero coverage.
    /// </summary>
    [Fact]
    public void Catalog_DiscoversEveryCuratedPage()
    {
        var all = DocExampleCatalog.AllPages();
        foreach (var (docPath, count) in CuratedPages())
        {
            all.ContainsKey(docPath).Should().BeTrue($"the sweep catalog must see the curated doc page {docPath}");
            all[docPath].Should().BeGreaterThanOrEqualTo(count,
                $"the fence parser must find at least the curated number of --render examples on {docPath}");
        }
    }

    /// <summary>And the escaped teaching samples must NOT count (the AuthoringDocumentation trap).</summary>
    [Fact]
    public void Catalog_EscapedTeachingSamples_DoNotCount()
    {
        var all = DocExampleCatalog.AllPages();
        all.ContainsKey("Doc/Architecture/AuthoringDocumentation").Should().BeTrue();
        all["Doc/Architecture/AuthoringDocumentation"].Should().Be(0,
            "--render inside an escaped ```text teaching fence is not an executable example");
    }

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(SweepPages))]
    public async Task DocPage_Examples_MountAndRenderWithoutErrors(string docPath, int exampleCount)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        // Pages whose examples need a LANGUAGE WORKER can only execute where it is deployed — the
        // e2e stack ships no python gate, so skip EXPLICITLY (a fail here would blame the page).
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("E2E_PYTHON") != "1"
                && DocExampleCatalog.AllPageExamples().TryGetValue(docPath, out var ex)
                && DocExampleCatalog.NeedsLanguageWorker(docPath, ex),
            $"{docPath} needs a language worker (python) — the e2e stack has none (set E2E_PYTHON=1 when it does)");

        // ONE shared authenticated context for the whole sweep (owned by the fixture): a fresh
        // context + /dev/signin per page starved the portal under the sweep's kernel-compile load.
        var context = await fixture.SharedAuthenticatedContextAsync();
        // Pay the one-time Roslyn warm-up up front, not on whichever page runs first.
        await fixture.EnsureKernelWarmAsync(context);
        var page = await context.NewPageAsync();
        try
        {
        await page.SetViewportSizeAsync(1400, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{docPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // 1. One .layout-area placeholder per --render block (at-least-N, attached).
        var areas = page.Locator(".layout-area");
        await areas.Nth(exampleCount - 1).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 90_000 });

        // 2. Every example leaves the loading state: the area has real children and no progress
        //    ring. Generous budget — the first kernel cell pays Roslyn warm-up / nuget restores.
        await page.WaitForFunctionAsync($$"""
            () => {
              const areas = [...document.querySelectorAll('.layout-area')].slice(0, {{exampleCount}});
              return areas.length >= {{exampleCount}} && areas.every(a =>
                !a.querySelector('fluent-progress-ring') &&
                (a.children.length > 0 || a.textContent.trim().length > 0));
            }
            """, null, new PageWaitForFunctionOptions { Timeout = 150_000 });

        Directory.CreateDirectory("/tmp/doc-examples-sweep");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"/tmp/doc-examples-sweep/{docPath.Replace('/', '-')}.png",
            FullPage = true
        });

        // 3. No error placeholders (the DocExamplesRenderTest contract, verbatim).
        (await areas.GetByText("Area not found").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render the 'Area not found' placeholder");
        (await areas.GetByText("No renderer is registered").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render the 'No renderer is registered' placeholder");
        (await page.Locator(".layout-area-error").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render a 'Layout Area Error' div");
        (await page.Locator(".meshweaver-render-error").CountAsync()).Should().Be(0,
            $"no example on {docPath} may trip the per-area ErrorBoundary ('This area failed to render')");
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

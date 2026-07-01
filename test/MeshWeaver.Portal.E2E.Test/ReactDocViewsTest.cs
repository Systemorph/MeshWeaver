using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// React-frontend parity scorecard: renders every documentation page with embedded interactive
/// examples through the REACT portal (the SPA served under <c>/app/</c>) and asserts each page
/// reaches real rendered content — no offline fallback, no live-stream error, and (where the page
/// has one) the kernel-executed output marker visible.
///
/// <para><b>Routing shape</b>: the React portal uses hash-based routing over mesh paths —
/// <c>{E2E_BASE_URL}/app/#/{meshPath}</c> renders that node's DEFAULT layout area live over
/// gRPC-web (the same area the Blazor portal renders at <c>/{meshPath}</c>). No hash renders the
/// signed-in user's home. See <c>clients/portal/src/Portal.tsx</c> / <c>live.ts</c>.</para>
///
/// <para><b>Auth</b>: the SPA mints a short-lived API token off the browser session cookie
/// (<c>POST /api/tokens</c>) and joins the mesh over same-origin gRPC-web — so the test context
/// only needs the fixture's DevLogin cookie, exactly like the Blazor tests.</para>
///
/// <para><b>Opt-in gate</b>: the whole class skips unless <c>E2E_REACT=true</c>. The React
/// renderer is EXPECTED to fail on many of these pages while parity catches up (embedded
/// layout-area examples, kernel-rendered controls, …) — this suite is the scorecard that measures
/// the gap, mirroring the Blazor-side <c>DocExamplesRenderTest</c> page-by-page.</para>
///
/// <para>Assertion strategy (per page):
/// <list type="number">
///   <item>Wait for the live area shell (<c>[data-mw-live-area]</c>) — proves the SPA connected
///     (did not fall back to the offline sample) and routed the doc path.</item>
///   <item>Assert the ABSENCE of the failure placeholders: <c>[data-mw-offline]</c> (connection
///     fell back to bundled sample data) and <c>[data-mw-area-error]</c> (the live layout-area
///     stream faulted).</item>
///   <item>Wait for REAL content: the page's rendered-output text marker (kernel-executed output,
///     DOM-agnostic — the same markers <c>DocExamplesRenderTest</c> uses) when the page has one;
///     otherwise any non-trivial rendered DOM beyond the "Building layout…" progress frame.</item>
/// </list></para>
/// </summary>
[Collection("portal-e2e")]
public class ReactDocViewsTest(PortalFixture fixture)
{
    private static bool ReactEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("E2E_REACT"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every doc page with embedded interactive examples — the same page list as the Blazor-side
    /// <c>DocExamplesRenderTest.ExamplePages</c> (enumerated from
    /// <c>grep -rl -- "--render" src/MeshWeaver.Documentation/Data</c>). Columns: doc path ·
    /// rendered-output text marker (nullable; substring, matched inside the live area only — the
    /// Blazor CSS selectors are dropped here because the React renderer emits different DOM).
    /// </summary>
    public static TheoryData<string, string?> ExamplePages => new()
    {
        // ---- GUI ----
        { "Doc/GUI", "live from the kernel" },
        { "Doc/GUI/LayoutAreas", "live render" },
        { "Doc/GUI/LayoutGrid", "12 cols (100%)" },
        { "Doc/GUI/DataGrid", "Widget" },
        { "Doc/GUI/ContainerControl", "Welcome to the app" },
        { "Doc/GUI/ContainerControl/Stack", "Stack layout demo" },
        { "Doc/GUI/ContainerControl/Tabs", "General settings here" },
        { "Doc/GUI/ContainerControl/Toolbar", null },
        { "Doc/GUI/ContainerControl/Splitter", "Left Panel" },
        { "Doc/GUI/Observables", "This text never changes" },
        { "Doc/GUI/Editor", "Date/time picker" },
        { "Doc/GUI/DataBinding", "NumberFieldControl" },
        { "Doc/GUI/DataBinding/ItemTemplate", "Alice" },
        { "Doc/GUI/Attributes", "Attribute Quick Reference" },
        { "Doc/GUI/NodeMenu", "Permission" },
        { "Doc/GUI/ReactiveDialogs", "Distribution Statistics" },
        { "Doc/GUI/SidePanel", "Side Panel Header Controls" },
        // ---- Architecture ----
        { "Doc/Architecture/UserInterface", "Controls Language" },
        { "Doc/Architecture/UserInterface/AvailableControls", "live in the kernel" },
        { "Doc/Architecture/ScriptExecution", "Rebound per submission" },
        { "Doc/Architecture/ScriptExecutionDemo", "🎆" },
        { "Doc/Architecture/VectorSearch", "TextSearch" },
        { "Doc/Architecture/BusinessRules", "C001" },
        // ---- AI ----
        { "Doc/AI/ExecuteScript", "Typical agent flow" },
        // ---- DataMesh ----
        { "Doc/DataMesh/InteractiveMarkdown", "Hello World" },
        { "Doc/DataMesh/QuerySyntax", "nodeType:Organization" },
        { "Doc/DataMesh/DataConfiguration", "Data Configuration Quick Reference" },
        { "Doc/DataMesh/DataModeling", "Common Data Modeling Attributes" },
        { "Doc/DataMesh/DataCubes", "Funding Ratio" },
        { "Doc/DataMesh/NodeTypes", "This cell rendered at" },
        { "Doc/DataMesh/NodeTypeConfiguration", "NodeType summary card" },
        { "Doc/DataMesh/CreatingNodeTypes", "Primary key field" },
        { "Doc/DataMesh/NodeOperations", "Node Operations at a Glance" },
        { "Doc/DataMesh/CRUD", "Single entity by ID" },
        { "Doc/DataMesh/VirtualDataSources", "Mesh query mirror" },
        { "Doc/DataMesh/NugetPackages", "hello world framework" },
        { "Doc/DataMesh/NodeTypeWithNuGet", "Determinant" },
        { "Doc/DataMesh/UnifiedPath/ContentPrefix", "Collection Prefix" },
        { "Doc/DataMesh/UnifiedPath/AreaPrefix", "Thumbnail area" },
        { "Doc/DataMesh/UnifiedPath/DataPrefix", "All Products" },
    };

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(ExamplePages))]
    public async Task ReactPortal_DocPage_RendersRealContent(string docPath, string? renderedTextMarker)
    {
        Assert.SkipUnless(ReactEnabled,
            "React parity scorecard is opt-in — set E2E_REACT=true to run it against a portal serving the React SPA under /app/.");
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        // NOTE: never WaitUntil=NetworkIdle here — the SPA holds the gRPC-web Connect stream open
        // for its whole life, so the network never goes idle.
        await page.GotoAsync($"{fixture.BaseUrl}/app/#/{docPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 60_000 });

        // 1. The live shell must engage: the SPA minted a token and joined the mesh over gRPC-web.
        var liveArea = page.Locator("[data-mw-live-area]");
        await liveArea.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });

        // 2. No failure placeholders: offline fallback = the connection/auth path is broken (a
        //    wiring defect, not a parity gap); area error = the live layout-area stream faulted.
        (await page.Locator("[data-mw-offline]").CountAsync()).Should().Be(0,
            $"the React portal must reach the live mesh for {docPath}, not fall back to offline sample mode");
        (await page.Locator("[data-mw-area-error]").CountAsync()).Should().Be(0,
            $"the live layout-area stream for {docPath} must not fault");

        // 3. REAL rendered content inside the live area. The generous timeout covers the kernel's
        //    first-cell Roslyn warm-up on pages whose marker is kernel-executed output.
        if (renderedTextMarker is not null)
        {
            await liveArea.GetByText(renderedTextMarker).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
        }
        else
        {
            // No page-specific marker — wait until the area rendered something beyond the
            // "Building layout…" progress frame.
            await page.WaitForFunctionAsync(
                """
                () => {
                  const el = document.querySelector('[data-mw-live-area]');
                  if (!el) return false;
                  const text = (el.innerText || '').trim();
                  return text.length > 0 && !text.includes('Building layout');
                }
                """,
                null,
                new PageWaitForFunctionOptions { Timeout = 120_000 });
        }

        Directory.CreateDirectory("/tmp/react-doc-views");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"/tmp/react-doc-views/{docPath.Replace('/', '-')}.png",
            FullPage = true
        });

        // Re-check the error placeholders AFTER content settled (a late stream fault must fail too).
        (await page.Locator("[data-mw-area-error]").CountAsync()).Should().Be(0,
            $"the live layout-area stream for {docPath} must stay healthy after rendering");
    }
}

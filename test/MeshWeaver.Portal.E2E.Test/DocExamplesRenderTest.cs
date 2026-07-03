using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Renders every documentation page that embeds interactive examples and asserts each example
/// actually rendered REAL content — not an error placeholder and not an eternal skeleton.
///
/// <para>Doc pages (served under <c>/Doc/…</c> from <c>src/MeshWeaver.Documentation/Data</c>) embed
/// live examples as fenced code blocks with <c>--render &lt;AreaName&gt;</c>. On page load the
/// markdown renderer emits one <c>div.layout-area</c> placeholder per block
/// (<c>ExecutableCodeBlockRenderer</c> → <c>LayoutAreaMarkdownRenderer.GetLayoutAreaDiv</c>), the
/// kernel executes each cell in document order, and the block's return value renders live into the
/// area via <c>LayoutAreaView</c>.</para>
///
/// <para>Assertion strategy (per page):
/// <list type="number">
///   <item>Wait until at least the expected number of <c>.layout-area</c> placeholders are attached
///     (proves the markdown pipeline recognized every <c>--render</c> block).</item>
///   <item>Wait for a control-specific DOM marker INSIDE <c>.layout-area</c> — a FluentUI element
///     (<c>fluent-tabs</c>, <c>fluent-toolbar</c>, <c>.fluent-grid</c>, <c>.fluent-multi-splitter</c>,
///     <c>.stack-*</c>, a data-grid <c>table</c>) and/or a rendered-OUTPUT text that only exists once
///     the kernel executed. Both are scoped under <c>.layout-area</c> so the <c>--show-code</c> source
///     display (which sits in a sibling <c>div.code-content</c>) can never false-pass the assertion —
///     the HomeChatExecuteTest lesson: assert rendered DOM, never source echoes.</item>
///   <item>Explicitly assert the ABSENCE of every error placeholder: the "Area not found" /
///     "No renderer is registered" markdown placeholder (<c>LayoutDefinition</c>), the
///     <c>.layout-area-error</c> div ("Layout Area Error"), and the <c>.meshweaver-render-error</c>
///     ErrorBoundary fallback ("This area failed to render").</item>
/// </list></para>
///
/// <para>Run against the throwaway e2e portal (see .claude/skills/playwright/SKILL.md):
/// <c>memex-local e2e up</c> → <c>memex-local e2e test DocExamplesRenderTest</c>. The kernel demos
/// need no language model, only a working kernel — the standalone monolith
/// (<c>E2E_BASE_URL=https://localhost:7122</c>) works too.</para>
/// </summary>
[Collection("portal-e2e")]
public class DocExamplesRenderTest(PortalFixture fixture)
{
    // Control-specific DOM markers, always scoped under the .layout-area embed wrapper.
    // FluentUI Blazor renders: Stack → div.stack-horizontal/.stack-vertical, LayoutGrid → .fluent-grid,
    // MultiSplitter → .fluent-multi-splitter, Tabs → <fluent-tabs>, Toolbar → <fluent-toolbar>,
    // DataGrid → fluent-data-grid (element or class, version-dependent) with an inner <table>.
    private const string Stack = ".layout-area .stack-horizontal, .layout-area .stack-vertical";
    private const string Grid = ".layout-area .fluent-grid";
    private const string Tabs = ".layout-area fluent-tabs";
    private const string Toolbar = ".layout-area fluent-toolbar";
    private const string Splitter = ".layout-area .fluent-multi-splitter";
    private const string DataGrid = ".layout-area fluent-data-grid, .layout-area .fluent-data-grid, .layout-area table";
    private const string Table = ".layout-area table";
    private const string MarkdownBody = ".layout-area .markdown-body";

    /// <summary>
    /// Every doc page with embedded interactive examples (enumerated from
    /// <c>grep -rl -- "--render" src/MeshWeaver.Documentation/Data</c>; AuthoringDocumentation.md is
    /// excluded — its <c>--render</c> appears only inside an escaped <c>```text</c> teaching sample).
    /// Columns: doc path · number of <c>--render</c> examples on the page · control-specific CSS
    /// marker (nullable) · rendered-output text marker (nullable; substring, case-insensitive,
    /// matched inside <c>.layout-area</c> only). At least one marker is always present.
    /// </summary>
    public static TheoryData<string, int, string?, string?> ExamplePages => new()
    {
        // ---- GUI ----
        { "Doc/GUI", 3, Stack, "live from the kernel" },
        { "Doc/GUI/LayoutAreas", 1, Stack, "live render" },
        { "Doc/GUI/LayoutGrid", 4, Grid, "12 cols (100%)" },
        { "Doc/GUI/DataGrid", 6, DataGrid, "Widget" },
        { "Doc/GUI/ContainerControl", 2, Tabs, "Welcome to the app" },
        { "Doc/GUI/ContainerControl/Stack", 3, Stack, "Stack layout demo" },
        { "Doc/GUI/ContainerControl/Tabs", 3, Tabs, "General settings here" },
        { "Doc/GUI/ContainerControl/Toolbar", 5, Toolbar, null },
        { "Doc/GUI/ContainerControl/Splitter", 8, Splitter, "Left Panel" },
        { "Doc/GUI/Observables", 3, Stack, "This text never changes" },
        { "Doc/GUI/Editor", 1, Stack, "Date/time picker" },
        { "Doc/GUI/DataBinding", 1, Table, "NumberFieldControl" },
        { "Doc/GUI/DataBinding/ItemTemplate", 1, Stack, "Alice" },
        { "Doc/GUI/Attributes", 1, Stack, "Attribute Quick Reference" },
        { "Doc/GUI/NodeMenu", 1, Table, "Permission" },
        { "Doc/GUI/ReactiveDialogs", 1, Stack, "Distribution Statistics" },
        { "Doc/GUI/SidePanel", 1, Stack, "Side Panel Header Controls" },
        // ---- Architecture ----
        { "Doc/Architecture/UserInterface", 1, Stack, "Controls Language" },
        { "Doc/Architecture/UserInterface/AvailableControls", 9, Stack, "live in the kernel" },
        { "Doc/Architecture/NativeMauiRendering", 1, Tabs, null },
        { "Doc/Architecture/ScriptExecution", 1, Table, "Rebound per submission" },
        { "Doc/Architecture/ScriptExecutionDemo", 2, null, "🎆" },
        { "Doc/Architecture/VectorSearch", 1, Table, "TextSearch" },
        { "Doc/Architecture/BusinessRules", 1, Table, "C001" },
        // ---- AI ----
        { "Doc/AI/ExecuteScript", 1, Table, "Typical agent flow" },
        // ---- DataMesh ----
        { "Doc/DataMesh/InteractiveMarkdown", 3, null, "Hello World" },
        { "Doc/DataMesh/QuerySyntax", 1, Table, "nodeType:Organization" },
        { "Doc/DataMesh/DataConfiguration", 1, Stack, "Data Configuration Quick Reference" },
        { "Doc/DataMesh/DataModeling", 2, Stack, "Common Data Modeling Attributes" },
        { "Doc/DataMesh/DataCubes", 5, Table, "Funding Ratio" },
        { "Doc/DataMesh/NodeTypes", 1, Stack, "This cell rendered at" },
        { "Doc/DataMesh/NodeTypeConfiguration", 1, Table, "NodeType summary card" },
        { "Doc/DataMesh/CreatingNodeTypes", 1, Table, "Primary key field" },
        { "Doc/DataMesh/NodeOperations", 1, Stack, "Node Operations at a Glance" },
        { "Doc/DataMesh/CRUD", 1, Table, "Single entity by ID" },
        { "Doc/DataMesh/VirtualDataSources", 1, Table, "Mesh query mirror" },
        { "Doc/DataMesh/NugetPackages", 4, null, "hello world framework" },
        { "Doc/DataMesh/NodeTypeWithNuGet", 1, MarkdownBody, "Determinant" },
        { "Doc/DataMesh/UnifiedPath/ContentPrefix", 1, Stack, "Collection Prefix" },
        { "Doc/DataMesh/UnifiedPath/AreaPrefix", 1, Stack, "Thumbnail area" },
        { "Doc/DataMesh/UnifiedPath/DataPrefix", 1, Table, "All Products" },
    };

    [Theory(Timeout = 240_000)]
    [MemberData(nameof(ExamplePages))]
    public async Task DocPage_InteractiveExamples_RenderRealContent(
        string docPath, int exampleCount, string? controlSelector, string? renderedTextMarker)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{docPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // 1. One .layout-area placeholder per --render block. Nth().WaitForAsync is an
        //    at-least-N wait (attached, not visible: a still-loading area may have zero height).
        var areas = page.Locator(".layout-area");
        await areas.Nth(exampleCount - 1).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 90_000 });

        // 2. REAL rendered content. Both markers are scoped inside .layout-area, so the
        //    --show-code source display (sibling div.code-content) can never satisfy them.
        //    The kernel executes cells in document order — the generous timeout covers the
        //    first-cell Roslyn warm-up and #r nuget restores (NugetPackages / NodeTypeWithNuGet).
        if (controlSelector is not null)
            await page.Locator(controlSelector).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
        if (renderedTextMarker is not null)
            await areas.GetByText(renderedTextMarker).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });

        Directory.CreateDirectory("/tmp/doc-examples");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"/tmp/doc-examples/{docPath.Replace('/', '-')}.png",
            FullPage = true
        });

        // 3. No error placeholders anywhere in the page's embedded areas.
        (await areas.GetByText("Area not found").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render the 'Area not found' placeholder");
        (await areas.GetByText("No renderer is registered").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render the 'No renderer is registered' placeholder");
        (await page.Locator(".layout-area-error").CountAsync()).Should().Be(0,
            $"no example on {docPath} may render a 'Layout Area Error' div");
        (await page.Locator(".meshweaver-render-error").CountAsync()).Should().Be(0,
            $"no example on {docPath} may trip the per-area ErrorBoundary ('This area failed to render')");
    }
}

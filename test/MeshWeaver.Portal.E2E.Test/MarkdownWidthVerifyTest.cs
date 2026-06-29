using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Visual verification that a markdown node page renders full width (not the centered 1200px reading
/// column) after the GetContainerStyle / MarkdownOverview change. Measured at a wide viewport where the
/// difference is obvious.
/// </summary>
[Collection("portal-e2e")]
public class MarkdownWidthVerifyTest(PortalFixture fixture)
{
    [Fact(Timeout = 120_000)]
    public async Task Markdown_Page_RendersFullWidth()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/Doc/DataMesh/CollaborativeEditing",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator(".collab-md-container").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.WaitForTimeoutAsync(2000);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/md-page.png", FullPage = true });

        var measure = await page.EvaluateAsync<string>("""
            () => {
              const w = el => el ? Math.round(el.getBoundingClientRect().width) : -1;
              const container = document.querySelector('.collab-md-container');
              const pageEl = document.querySelector('.body-content, main, body');
              return JSON.stringify({ content: w(container), page: w(pageEl) });
            }
            """);
        System.IO.File.WriteAllText("/tmp/md-widths.txt", measure);

        var doc = System.Text.Json.JsonDocument.Parse(measure).RootElement;
        float contentW = doc.GetProperty("content").GetSingle();
        float pageW = doc.GetProperty("page").GetSingle();
        (contentW / pageW).Should().BeGreaterThan(0.9f,
            $"markdown should render full width, not the 1200px reading column (content={contentW:F0}px of {pageW:F0}px)");
    }
}

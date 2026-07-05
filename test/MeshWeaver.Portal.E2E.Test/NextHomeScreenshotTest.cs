using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Investigation harness (NOT a pass/fail assertion): loads the portal-next `/next` home several
/// times and captures a full-page screenshot + the browser console each reload, so the developer can
/// SEE the render (the "random subset / renders prematurely" report, missing icons, missing margins)
/// instead of relying on the user to eyeball it. Screenshots + console land in the session scratchpad.
/// </summary>
[Collection("portal-e2e")]
public class NextHomeScreenshotTest(PortalFixture fixture)
{
    private const string OutDir =
        "/private/tmp/claude-501/-Users-roland-code-MeshWeaver/f53d6845-e683-4096-a161-c966ac4b8ffe/scratchpad/shots";

    [Fact(Timeout = 300_000)]
    public async Task Home_Screenshots_AcrossReloads()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Directory.CreateDirectory(OutDir);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1100);

        var console = new List<string>();
        page.Console += (_, m) => console.Add($"[{m.Type}] {m.Text}");
        page.Response += (_, r) =>
        {
            if (r.Status >= 400) console.Add($"[HTTP {r.Status}] {r.Url}");
        };

        for (var i = 0; i < 4; i++)
        {
            // DOMContentLoaded, NOT NetworkIdle — the live gRPC-web Connect stream stays open, so the
            // page never goes network-idle (a long-lived stream is the intended state).
            await page.GotoAsync($"{fixture.BaseUrl}/next",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            // Let the live stream fold its frames (or fail) before the shot.
            await page.WaitForTimeoutAsync(8000);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"{OutDir}/home-{i}.png", FullPage = true });
            await File.WriteAllLinesAsync($"{OutDir}/console.log", console); // flush each round in case of a later throw
        }

        await File.WriteAllLinesAsync($"{OutDir}/console.log", console);
    }
}

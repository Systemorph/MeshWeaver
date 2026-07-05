using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Investigation harness (NOT a pass/fail assertion): for a set of representative screens it captures
/// a full-page screenshot of BOTH the Blazor portal (served at the origin root) and the React/Next
/// frontend (served under <c>/next</c>) of the SAME running app, so a developer can eyeball where the
/// <c>/next</c> shell renders WORSE than Blazor (missing content, broken layout, dead controls) before
/// it ships. Screenshots + per-screen Next console/HTTP logs land in the session scratchpad.
///
/// <para>The React frontend has a known "random subset renders on some loads" server-side sync-hub
/// race, so every Next capture RELOADS a few times and keeps the richest (live + most text) render —
/// a transient blank is a flake, not a genuine "missing content" regression.</para>
///
/// <para>The settings screen additionally PROBES the "settings tabs have no effect in Next" report:
/// it enumerates the tab controls (Fluent <c>[role='tab']</c> or the Settings nav-menu anchors), clicks
/// each, and records whether the content pane text actually changed.</para>
/// </summary>
[Collection("portal-e2e")]
public class BlazorVsNextCompareTest(PortalFixture fixture)
{
    private static readonly string OutDir =
        Path.Combine(Path.GetTempPath(), "meshweaver-e2e-compare");

    [Fact(Timeout = 600_000)]
    public async Task Compare_Blazor_Vs_Next_AcrossScreens()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Directory.CreateDirectory(OutDir);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var user = fixture.UserId; // resolved from the token mint on the first authenticated context
        var notes = new List<string> { $"# Blazor-vs-Next comparison — user='{user}' baseUrl={fixture.BaseUrl}" };

        // 1. HOME — / vs /next
        await CaptureBlazorAsync(context, "home", "/");
        notes.Add(await CaptureNextBestAsync(context, "home", "/next"));

        // 2. SETTINGS — /{user}/Settings vs /next/{user}/Settings
        var settingsBlazor = $"/{user}/Settings";
        var settingsNext = $"/next/{user}/Settings";
        await CaptureBlazorAsync(context, "settings", settingsBlazor);
        notes.Add(await CaptureNextBestAsync(context, "settings", settingsNext));
        notes.Add("");
        notes.AddRange(await ProbeSettingsTabsAsync(context, settingsNext));
        notes.Add("");

        // 3. DOC — /Doc/GUI vs /next/Doc/GUI
        await CaptureBlazorAsync(context, "doc", "/Doc/GUI");
        notes.Add(await CaptureNextBestAsync(context, "doc", "/next/Doc/GUI"));

        // 4. SEARCH — /search?q=nodeType:Agent vs /next/search?q=nodeType:Agent
        await CaptureBlazorAsync(context, "search", "/search?q=nodeType:Agent");
        notes.Add(await CaptureNextBestAsync(context, "search", "/next/search?q=nodeType:Agent"));

        // 5. AGENT — resolve an agent node path (mesh query, else scrape a search result), else skip.
        var agentPath = await FindAgentPathAsync(context);
        if (!string.IsNullOrEmpty(agentPath))
        {
            notes.Add($"# agent node resolved: {agentPath}");
            await CaptureBlazorAsync(context, "agent", $"/{agentPath}");
            notes.Add(await CaptureNextBestAsync(context, "agent", $"/next/{agentPath}"));
        }
        else
        {
            notes.Add("# agent: no Agent node resolved (mesh query + search scrape both empty) — SKIPPED");
        }

        await File.WriteAllLinesAsync($"{OutDir}/notes.txt", notes);
    }

    // ── Blazor capture — single load, full page. ────────────────────────────────────────────────
    private async Task CaptureBlazorAsync(IBrowserContext context, string screen, string path)
    {
        var page = await context.NewPageAsync();
        try
        {
            await page.SetViewportSizeAsync(1600, 1100);
            await page.GotoAsync($"{fixture.BaseUrl}{path}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90_000 });
            // Let the Blazor circuit render the area (SignalR handshake + first frame).
            await page.WaitForTimeoutAsync(8000);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"{OutDir}/{screen}-blazor.png", FullPage = true });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // ── Next capture — reload a few times, keep the richest (live + most text) render. ───────────
    private async Task<string> CaptureNextBestAsync(IBrowserContext context, string screen, string path)
    {
        var console = new List<string>();
        var page = await context.NewPageAsync();
        try
        {
            await page.SetViewportSizeAsync(1600, 1100);
            page.Console += (_, m) =>
            {
                if (m.Type is "error" or "warning") console.Add($"[{m.Type}] {m.Text}");
            };
            page.Response += (_, r) =>
            {
                if (r.Status >= 400) console.Add($"[HTTP {r.Status}] {r.Url}");
            };

            var bestScore = -1L;
            var bestDetail = "no render captured";
            var flaky = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}{path}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                // DOMContentLoaded then settle: the live gRPC-web stream folds its first frame (or fails).
                await page.WaitForTimeoutAsync(8000);

                var len = await page.EvaluateAsync<int>(
                    "() => (document.querySelector('[data-mw-live-area]')?.innerText || document.body.innerText || '').length");
                var isLive = await page.EvaluateAsync<bool>(
                    "() => document.querySelector('[data-mw-live-area][data-mw-live=\"true\"]') != null");
                if (!isLive || len < 120) flaky = true; // a blank/partial attempt

                // Prefer a live render; among those the one with the most text.
                var score = (isLive ? 1_000_000L : 0L) + len;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDetail = $"attempt {attempt}: live={isLive} textLen={len}";
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"{OutDir}/{screen}-next.png", FullPage = true });
                }
                if (isLive && len > 400) break; // good enough — stop retrying
            }

            var summary = $"{screen}-next: best={bestDetail}{(flaky ? "  [FLAKY: at least one attempt was blank/partial]" : "")}";
            console.Insert(0, $"# {summary}");
            await File.WriteAllLinesAsync($"{OutDir}/{screen}-next.console.log", console);
            return summary;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // ── Settings tab probe — the "tabs have no effect in Next" verification. ─────────────────────
    private async Task<List<string>> ProbeSettingsTabsAsync(IBrowserContext context, string nextSettingsPath)
    {
        var notes = new List<string> { "=== Settings tab probe (Next) ===" };
        var page = await context.NewPageAsync();
        try
        {
            await page.SetViewportSizeAsync(1600, 1100);
            // Reload until the settings live area renders its tab controls (defeats the partial-render flake).
            var tabCount = 0;
            var usingRoleTabs = false;
            for (var attempt = 0; attempt < 3 && tabCount == 0; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}{nextSettingsPath}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await page.WaitForTimeoutAsync(8000);
                var roleTabs = await page.Locator("[data-mw-live-area] [role='tab']").CountAsync();
                var navTabs = await page.Locator("[data-mw-live-area] a[href*='Settings']").CountAsync();
                usingRoleTabs = roleTabs > 0;
                tabCount = roleTabs > 0 ? roleTabs : navTabs;
                notes.Add($"attempt {attempt}: [role='tab']={roleTabs}, settings nav anchors={navTabs}");
            }

            if (tabCount == 0)
            {
                notes.Add("NO tab controls found in the Next settings live area (neither [role='tab'] nor Settings nav anchors).");
                return notes;
            }

            notes.Add($"Using {(usingRoleTabs ? "[role='tab']" : "Settings nav anchors")} — {tabCount} tab(s). Clicking each:");

            // Enumerate labels + hrefs once for a stable list, then click each from a FRESH load so a
            // full-page-navigation escape (the suspected bug) doesn't leave a stale element for the next.
            var selector = usingRoleTabs
                ? "[data-mw-live-area] [role='tab']"
                : "[data-mw-live-area] a[href*='Settings']";
            var labels = new List<string>();
            var hrefs = new List<string?>();
            for (var i = 0; i < tabCount; i++)
            {
                var el = page.Locator(selector).Nth(i);
                labels.Add((await el.InnerTextAsync()).Trim().Replace("\n", " "));
                hrefs.Add(usingRoleTabs ? null : await el.GetAttributeAsync("href"));
            }

            for (var i = 0; i < tabCount; i++)
            {
                // Fresh load so each click is isolated from a prior escape/navigation.
                await page.GotoAsync($"{fixture.BaseUrl}{nextSettingsPath}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await page.WaitForTimeoutAsync(6000);

                var before = await ContentPaneTextAsync(page);
                var urlBefore = page.Url;
                var el = page.Locator(selector).Nth(i);
                string clickResult;
                try
                {
                    await el.ClickAsync(new LocatorClickOptions { Timeout = 6000 });
                    clickResult = "clicked";
                }
                catch (Exception ex)
                {
                    notes.Add($"  tab[{i}] '{labels[i]}' href='{hrefs[i]}' — CLICK FAILED: {ex.Message.Split('\n')[0]}");
                    continue;
                }
                await page.WaitForTimeoutAsync(3000);
                var after = await ContentPaneTextAsync(page);
                var urlAfter = page.Url;
                var contentChanged = !string.Equals(before, after, StringComparison.Ordinal);
                var urlChanged = !string.Equals(urlBefore, urlAfter, StringComparison.Ordinal);
                notes.Add(
                    $"  tab[{i}] '{labels[i]}' href='{hrefs[i]}' -> {clickResult}; " +
                    $"urlChanged={urlChanged} ({Rel(urlBefore)} -> {Rel(urlAfter)}); " +
                    $"contentChanged={contentChanged} (paneTextLen {before.Length} -> {after.Length})");
            }
        }
        finally
        {
            await page.CloseAsync();
        }
        return notes;
    }

    /// <summary>Right-pane text of the settings splitter (falls back to whole live area — React drops
    /// the settings-content-pane class, so the specific selector may be absent).</summary>
    private static Task<string> ContentPaneTextAsync(IPage page) =>
        page.EvaluateAsync<string>(
            "() => { const el = document.querySelector('.settings-content-pane') " +
            "|| document.querySelector('[data-mw-live-area]') || document.body; return el.innerText || ''; }");

    private static string Rel(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return url;
        var slash = url.IndexOf('/', i + 3);
        return slash < 0 ? "/" : url[slash..];
    }

    /// <summary>Resolve an Agent node path: mesh query first (POST /api/mesh/query-nodes), else scrape a
    /// result-card href off the just-captured Blazor search page. Returns null when neither yields one.</summary>
    private async Task<string?> FindAgentPathAsync(IBrowserContext context)
    {
        try
        {
            var token = await fixture.MintTokenAsync(context);
            var resp = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/api/mesh/query-nodes",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
                    DataObject = new { query = "nodeType:Agent", limit = 5 },
                    Timeout = 25_000
                });
            if ((int)resp.Status < 400)
            {
                var text = await resp.TextAsync();
                if (!text.StartsWith("Error:", StringComparison.Ordinal) && !text.StartsWith("Not found:", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in results.EnumerateArray())
                        {
                            var p = (row.TryGetProperty("path", out var pe) ? pe.GetString() : null)
                                    ?? (row.TryGetProperty("Path", out var pe2) ? pe2.GetString() : null);
                            if (!string.IsNullOrWhiteSpace(p)) return p;
                        }
                    }
                }
            }
        }
        catch
        {
            // fall through to the DOM scrape
        }

        // Fallback: scrape an agent link off the Blazor search results page.
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{fixture.BaseUrl}/search?q=nodeType:Agent",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await page.WaitForTimeoutAsync(8000);
            var href = await page.EvaluateAsync<string?>(
                "() => { const a = Array.from(document.querySelectorAll('a[href]')).map(x => x.getAttribute('href'))" +
                ".find(h => h && /agent/i.test(h) && h.startsWith('/') && !h.startsWith('/next') && !h.startsWith('/search')); return a || null; }");
            return href?.TrimStart('/');
        }
        catch
        {
            return null;
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

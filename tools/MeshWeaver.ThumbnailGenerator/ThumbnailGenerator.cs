using Microsoft.Playwright;

namespace MeshWeaver.ThumbnailGenerator;

public static class ThumbnailGenerator
{
    public static async Task GenerateThumbnailsAsync(List<string> areaUrls, string outputDir, string baseUrl)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // Create a persistent context to maintain localStorage across pages
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1200, Height = 800 }
        });

        // First, set cookie consent in localStorage for the domain
        var tempPage = await context.NewPageAsync();
        await tempPage.GotoAsync(baseUrl);
        await tempPage.EvaluateAsync("() => localStorage.setItem('cookieConsent', 'accepted')");
        await tempPage.CloseAsync();

        var page = await context.NewPageAsync();

        var successCount = 0;
        var failureCount = 0;

        foreach (var url in areaUrls)
        {
            try
            {
                Console.WriteLine($"Generating thumbnail for: {url}");

                // Extract area name from URL for filename
                var uri = new Uri(url);
                var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var areaName = pathParts.LastOrDefault() ?? "unknown";
                var fileName = $"{areaName}.png";
                var filePath = Path.Combine(outputDir, fileName);

                // Navigate to the area URL
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                // Wait for content to load (similar to catalog logic)
                try
                {
                    await page.WaitForSelectorAsync("body[data-catalog-loaded='true']", new PageWaitForSelectorOptions
                    {
                        Timeout = 10000
                    });
                }
                catch (TimeoutException)
                {
                    // If no catalog loaded signal, wait for spinner to disappear or content to appear
                    try
                    {
                        await page.WaitForSelectorAsync("fluent-progress-ring", new PageWaitForSelectorOptions
                        {
                            State = WaitForSelectorState.Detached,
                            Timeout = 15000
                        });
                    }
                    catch (TimeoutException)
                    {
                        // Continue anyway - content might be loaded
                        Console.WriteLine($"  Warning: Loading indicators not found for {areaName}, proceeding...");
                    }
                }

                // Additional wait for content to stabilize
                await page.WaitForTimeoutAsync(2000);

                // Take screenshot of the full page or main content area
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = filePath,
                    Type = ScreenshotType.Png,
                    FullPage = false // Viewport screenshot for consistent sizing
                });

                Console.WriteLine($"  ✓ Saved thumbnail: {fileName}");
                successCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to generate thumbnail for {url}: {ex.Message}");
                failureCount++;
            }
        }

        await page.CloseAsync();

        Console.WriteLine($"\nThumbnail generation complete:");
        Console.WriteLine($"  Success: {successCount}");
        Console.WriteLine($"  Failed: {failureCount}");
        Console.WriteLine($"  Output directory: {outputDir}");
    }
}
using Microsoft.Playwright;

namespace MeshWeaver.ThumbnailGenerator;

public static class ThumbnailGenerator
{
    public static async Task GenerateThumbnailsAsync(List<string> areaUrls, string outputDir, string baseUrl, bool includeDarkMode = true)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var totalSuccessCount = 0;
        var totalFailureCount = 0;

        // Generate light mode thumbnails
        Console.WriteLine("📸 Generating light mode thumbnails...");
        var (lightSuccess, lightFailure) = await GenerateThumbnailsForMode(browser, areaUrls, outputDir, baseUrl, false);
        totalSuccessCount += lightSuccess;
        totalFailureCount += lightFailure;

        // Generate dark mode thumbnails if requested
        if (includeDarkMode)
        {
            Console.WriteLine("\n🌙 Generating dark mode thumbnails...");
            var (darkSuccess, darkFailure) = await GenerateThumbnailsForMode(browser, areaUrls, outputDir, baseUrl, true);
            totalSuccessCount += darkSuccess;
            totalFailureCount += darkFailure;
        }

        Console.WriteLine($"\n✅ Overall thumbnail generation complete:");
        Console.WriteLine($"  Success: {totalSuccessCount}");
        Console.WriteLine($"  Failed: {totalFailureCount}");
        Console.WriteLine($"  Output directory: {outputDir}");
    }

    private static async Task<(int successCount, int failureCount)> GenerateThumbnailsForMode(IBrowser browser, List<string> areaUrls, string outputDir, string baseUrl, bool isDarkMode)
    {
        // Create a persistent context to maintain localStorage across pages
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1400, Height = 1200 },
            ColorScheme = isDarkMode ? ColorScheme.Dark : ColorScheme.Light
        });

        // First, set cookie consent in localStorage for the domain
        var tempPage = await context.NewPageAsync();
        await tempPage.GotoAsync(baseUrl);
        await tempPage.EvaluateAsync("() => localStorage.setItem('cookieConsent', 'accepted')");
        await tempPage.CloseAsync();

        // Use parallel processing with limited concurrency to avoid overwhelming the server
        const int maxConcurrency = 4; // Adjust based on server capacity
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = areaUrls.Select(url => ProcessUrlAsync(context, url, outputDir, isDarkMode, semaphore)).ToArray();
        
        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);
        var failureCount = results.Count(r => !r);

        var modeText = isDarkMode ? "dark" : "light";
        Console.WriteLine($"\n{modeText.ToUpper()} mode generation complete:");
        Console.WriteLine($"  Success: {successCount}");
        Console.WriteLine($"  Failed: {failureCount}");
        
        return (successCount, failureCount);
    }

    private static async Task<bool> ProcessUrlAsync(IBrowserContext context, string url, string outputDir, bool isDarkMode, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var currentModeText = isDarkMode ? "dark" : "light";
            Console.WriteLine($"🔄 Generating {currentModeText} mode thumbnail for: {url}");

            // Create a new page for each URL to avoid conflicts
            var page = await context.NewPageAsync();
            try
            {
                // Extract area name from URL for filename
                var uri = new Uri(url);
                var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var areaName = pathParts.LastOrDefault() ?? "unknown";
                var fileName = isDarkMode ? $"{areaName}-dark.png" : $"{areaName}.png";
                var filePath = Path.Combine(outputDir, fileName);

                // Navigate to the area URL
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                // Simple content loading wait
                await WaitForContentLoad(page, areaName);

                // Additional wait for content to stabilize
                await page.WaitForTimeoutAsync(2000);

                // Take screenshot
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = filePath,
                    Type = ScreenshotType.Png,
                    FullPage = false
                });

                Console.WriteLine($"  ✅ Saved {currentModeText} thumbnail: {fileName}");
                return true;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            var currentModeText = isDarkMode ? "dark" : "light";
            Console.WriteLine($"  ❌ Failed {currentModeText} thumbnail for {url}: {ex.Message}");
            return false;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task WaitForContentLoad(IPage page, string areaName)
    {
        try
        {
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

        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Content load detection failed: {ex.Message}");
        }
    }

    private static async Task WaitForSelectorSafe(IPage page, string selector, int timeout, WaitForSelectorState state = WaitForSelectorState.Attached)
    {
        try
        {
            await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                Timeout = timeout,
                State = state
            });
        }
        catch (TimeoutException)
        {
            // Ignore timeout - this is expected behavior for optional selectors
        }
    }
}

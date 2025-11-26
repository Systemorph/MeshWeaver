using Microsoft.Playwright;

namespace MeshWeaver.ThumbnailGenerator;

public static class ThumbnailGenerator
{
    public static async Task GenerateThumbnailsAsync(List<string> areaUrls, string outputDir, string baseUrl, bool includeDarkMode = true, int thumbnailWidth = 400, int thumbnailHeight = 300)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var totalSuccessCount = 0;
        var totalFailureCount = 0;

        // Generate light mode thumbnails
        Console.WriteLine("=== Generating light mode thumbnails ===");
        var (lightSuccess, lightFailure) = await GenerateThumbnailsForMode(browser, areaUrls, outputDir, baseUrl, false, thumbnailWidth, thumbnailHeight);
        totalSuccessCount += lightSuccess;
        totalFailureCount += lightFailure;

        // Generate dark mode thumbnails if requested
        if (includeDarkMode)
        {
            Console.WriteLine("\n=== Generating dark mode thumbnails ===");
            var (darkSuccess, darkFailure) = await GenerateThumbnailsForMode(browser, areaUrls, outputDir, baseUrl, true, thumbnailWidth, thumbnailHeight);
            totalSuccessCount += darkSuccess;
            totalFailureCount += darkFailure;
        }

        Console.WriteLine("\nOverall thumbnail generation complete:");
        Console.WriteLine($"  Success: {totalSuccessCount}");
        Console.WriteLine($"  Failed: {totalFailureCount}");
        Console.WriteLine($"  Output directory: {outputDir}");
    }

    private static async Task<(int successCount, int failureCount)> GenerateThumbnailsForMode(IBrowser browser, List<string> areaUrls, string outputDir, string baseUrl, bool isDarkMode, int thumbnailWidth, int thumbnailHeight)
    {
        // Create a persistent context to maintain localStorage across pages
        // Use smaller viewport to ensure readable text size in thumbnails
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 800, Height = 600 },
            ColorScheme = isDarkMode ? ColorScheme.Dark : ColorScheme.Light,
            DeviceScaleFactor = 1.0f // Ensure consistent scaling
        });

        // First, set cookie consent in localStorage for the domain
        var tempPage = await context.NewPageAsync();
        await tempPage.GotoAsync(baseUrl);
        await tempPage.EvaluateAsync("() => localStorage.setItem('cookieConsent', 'accepted')");
        await tempPage.CloseAsync();

        // Use parallel processing with limited concurrency to avoid overwhelming the server
        const int maxConcurrency = 4; // Adjust based on server capacity
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = areaUrls.Select(url => ProcessUrlAsync(context, url, outputDir, isDarkMode, semaphore, thumbnailWidth, thumbnailHeight)).ToArray();
        
        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);
        var failureCount = results.Count(r => !r);

        var modeText = isDarkMode ? "dark" : "light";
        Console.WriteLine($"\n{modeText.ToUpper()} mode generation complete:");
        Console.WriteLine($"  Success: {successCount}");
        Console.WriteLine($"  Failed: {failureCount}");
        
        return (successCount, failureCount);
    }

    private static async Task<bool> ProcessUrlAsync(IBrowserContext context, string url, string outputDir, bool isDarkMode, SemaphoreSlim semaphore, int thumbnailWidth, int thumbnailHeight)
    {
        await semaphore.WaitAsync();
        try
        {
            var currentModeText = isDarkMode ? "dark" : "light";
            Console.WriteLine($"Generating {currentModeText} mode thumbnail for: {url}");

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

                // Try to capture the full content by measuring the page size and applying a zoom
                // so that the full content fits into the requested thumbnail dimensions. This
                // avoids fixed clipping that would miss content on pages that are larger/smaller.
                try
                {
                    var size = await page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth || document.body.scrollWidth, document.documentElement.scrollHeight || document.body.scrollHeight]");
                    var contentWidth = Math.Max(1, size[0]);
                    var contentHeight = Math.Max(1, size[1]);

                    // Compute scale to fit content into thumbnail while preserving aspect ratio.
                    var widthScale = (double)thumbnailWidth / contentWidth;
                    var heightScale = (double)thumbnailHeight / contentHeight;
                    // Choose the smaller scale so the whole content fits, but allow >1 to zoom up smaller content.
                    var scale = Math.Min(widthScale, heightScale);
                    if (double.IsInfinity(scale) || double.IsNaN(scale) || scale <= 0)
                        scale = 1.0;

                    // Apply CSS zoom to scale the page rendering (simple, broadly supported).
                    await page.EvaluateAsync("(s) => { document.documentElement.style.transformOrigin = '0 0'; document.documentElement.style.transform = 'scale(' + s + ')'; }", scale);

                    // Set viewport large enough to contain the scaled content (avoid cropping by the browser viewport).
                    var scaledWidth = (int)Math.Ceiling(contentWidth * scale);
                    var scaledHeight = (int)Math.Ceiling(contentHeight * scale);
                    var viewportWidth = Math.Max(thumbnailWidth, Math.Min(scaledWidth, 16384));
                    var viewportHeight = Math.Max(thumbnailHeight, Math.Min(scaledHeight, 16384));
                    await page.SetViewportSizeAsync(new ViewportSize { Width = viewportWidth, Height = viewportHeight });

                    // Take screenshot of the top-left area sized to the thumbnail; because we've scaled the page
                    // the thumbnail will approximately represent the full content. This keeps the output size stable.
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = filePath,
                        Type = ScreenshotType.Png,
                        FullPage = false,
                        Clip = new Clip
                        {
                            X = 0,
                            Y = 0,
                            Width = thumbnailWidth,
                            Height = thumbnailHeight
                        }
                    });
                }
                catch (Exception)
                {
                    // Fallback to a fixed clip screenshot if content measurement or scaling fails
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = filePath,
                        Type = ScreenshotType.Png,
                        FullPage = false,
                        Clip = new Clip
                        {
                            X = 0,
                            Y = 0,
                            Width = thumbnailWidth,
                            Height = thumbnailHeight
                        }
                    });
                }

                Console.WriteLine($"  Saved {currentModeText} thumbnail: {fileName}");
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
            Console.WriteLine($"  Failed {currentModeText} thumbnail for {url}: {ex.Message}");
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

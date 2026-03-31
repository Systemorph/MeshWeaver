using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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
        // Use large viewport to let content render at natural size before detection
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
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

                // Detect actual content dimensions at current viewport
                var contentDimensions = await DetectContentDimensions(page);
                Console.WriteLine($"  Detected content size: {contentDimensions.Width}×{contentDimensions.Height}");

                // Calculate final thumbnail dimensions that maintain aspect ratio
                var (thumbnailFinalWidth, thumbnailFinalHeight) = CalculateScaledDimensions(
                    contentDimensions.Width,
                    contentDimensions.Height,
                    thumbnailWidth,
                    thumbnailHeight
                );

                // Calculate intermediate viewport size (2x thumbnail for better quality)
                var intermediateWidth = thumbnailFinalWidth * 2;
                var intermediateHeight = thumbnailFinalHeight * 2;

                Console.WriteLine($"  Setting viewport to: {intermediateWidth}×{intermediateHeight} (2x for quality)");

                // Set viewport to intermediate size for better rendering quality
                await page.SetViewportSizeAsync(intermediateWidth, intermediateHeight);

                // Wait for page to re-render at new size
                await page.WaitForTimeoutAsync(1000);

                // Re-detect content dimensions after viewport resize to get actual rendered size
                var renderedDimensions = await DetectContentDimensions(page);
                var clipWidth = Math.Min(renderedDimensions.Width, intermediateWidth);
                var clipHeight = Math.Min(renderedDimensions.Height, intermediateHeight);

                // Take screenshot clipped to actual content size (avoids halo from empty viewport space)
                var tempFilePath = Path.Combine(outputDir, $"temp_{Guid.NewGuid()}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = tempFilePath,
                    Type = ScreenshotType.Png,
                    FullPage = false,
                    Clip = new Clip
                    {
                        X = 0,
                        Y = 0,
                        Width = clipWidth,
                        Height = clipHeight
                    }
                });

                // Resize to final thumbnail size with high quality
                using (var image = await SixLabors.ImageSharp.Image.LoadAsync(tempFilePath))
                {
                    image.Mutate(x => x.Resize(thumbnailFinalWidth, thumbnailFinalHeight));
                    await image.SaveAsPngAsync(filePath);
                }

                // Clean up temp file
                File.Delete(tempFilePath);

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

    private static async Task<(int Width, int Height)> DetectContentDimensions(IPage page)
    {
        // Get the actual rendered page dimensions
        var result = await page.EvaluateAsync<int[]>(@"() => {
            const body = document.body;
            const html = document.documentElement;

            // Get the maximum width and height from various sources
            const width = Math.max(
                body.scrollWidth || 0,
                body.offsetWidth || 0,
                html.clientWidth || 0,
                html.scrollWidth || 0,
                html.offsetWidth || 0
            );

            const height = Math.max(
                body.scrollHeight || 0,
                body.offsetHeight || 0,
                html.clientHeight || 0,
                html.scrollHeight || 0,
                html.offsetHeight || 0
            );

            return [width, height];
        }");

        return (result[0], result[1]);
    }

    private static (int Width, int Height) CalculateScaledDimensions(int contentWidth, int contentHeight, int maxWidth, int maxHeight)
    {
        // Calculate aspect ratio
        double contentAspectRatio = (double)contentWidth / contentHeight;
        double targetAspectRatio = (double)maxWidth / maxHeight;

        int scaledWidth, scaledHeight;

        // Scale to fit within max dimensions while preserving aspect ratio
        if (contentAspectRatio > targetAspectRatio)
        {
            // Content is wider - fit to width
            scaledWidth = maxWidth;
            scaledHeight = (int)(maxWidth / contentAspectRatio);
        }
        else
        {
            // Content is taller - fit to height
            scaledHeight = maxHeight;
            scaledWidth = (int)(maxHeight * contentAspectRatio);
        }

        return (scaledWidth, scaledHeight);
    }
}

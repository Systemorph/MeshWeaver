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
            ViewportSize = new ViewportSize { Width = 1200, Height = 800 },
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

                // Navigate to the area URL with optimized settings
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded, // Faster than NetworkIdle
                    Timeout = 15000 // Reduced from 30s
                });

                // Optimized content loading detection
                await WaitForContentLoad(page, areaName);

                // Reduced stabilization wait
                await page.WaitForTimeoutAsync(500); // Reduced from 2000ms

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
        Console.WriteLine($"  ⏳ Waiting for content to stabilize for {areaName}...");
        
        // Simple, reliable approach: wait for initial content, then give time for everything to load
        try
        {
            // First, wait for at least some content to appear (don't wait forever)
            try
            {
                await page.WaitForSelectorAsync("main, .content, [role='main'], .area-page-container", new PageWaitForSelectorOptions
                {
                    Timeout = 8000 // 8 seconds max wait for initial content
                });
                Console.WriteLine($"  ✅ Initial content detected for {areaName}");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"  ⚠️  No initial content selector found for {areaName}, proceeding anyway...");
            }
            
            // Give content time to fully render - this is the key for complex dashboards
            Console.WriteLine($"  🕐 Allowing 8 seconds for all areas to render completely...");
            await Task.Delay(8000);
            
            // Check for loading indicators and Google Maps specifically
            try
            {
                var loadingStatus = await page.EvaluateAsync<dynamic>(@"
                    () => {
                        // Check for common loading indicators
                        const spinners = document.querySelectorAll('fluent-progress-ring, .loading, .spinner, [data-area-loaded=""false""]');
                        
                        // Check for Google Maps elements and their loading state
                        const googleMapContainers = document.querySelectorAll('[id*=""google-map""], .google-map');
                        let googleMapsReady = true;
                        let googleMapsCount = googleMapContainers.length;
                        
                        googleMapContainers.forEach(container => {
                            const mapDiv = container.querySelector('div[style*=""position""]');
                            if (!mapDiv || mapDiv.children.length === 0) {
                                googleMapsReady = false;
                            }
                        });
                        
                        return {
                            hasSpinners: spinners.length > 0,
                            googleMapsCount: googleMapsCount,
                            googleMapsReady: googleMapsReady,
                            hasGoogleMapsScript: !!window.google && !!window.google.maps
                        };
                    }
                ");
                
                var hasSpinners = (bool)loadingStatus.hasSpinners;
                var googleMapsCount = (int)loadingStatus.googleMapsCount;
                var googleMapsReady = (bool)loadingStatus.googleMapsReady;
                var hasGoogleMapsScript = (bool)loadingStatus.hasGoogleMapsScript;
                
                if (googleMapsCount > 0)
                {
                    Console.WriteLine($"  🗺️  Detected {googleMapsCount} Google Maps element(s)");
                    
                    if (!hasGoogleMapsScript)
                    {
                        Console.WriteLine($"  ⏳ Google Maps API not loaded yet, waiting 6 seconds...");
                        await Task.Delay(6000);
                    }
                    else if (!googleMapsReady)
                    {
                        Console.WriteLine($"  ⏳ Google Maps not fully rendered, waiting 4 seconds...");
                        await Task.Delay(4000);
                    }
                    else
                    {
                        Console.WriteLine($"  ✅ Google Maps appear to be ready");
                    }
                }
                else if (hasSpinners)
                {
                    Console.WriteLine($"  ⏳ Loading indicators still present, waiting additional 4 seconds...");
                    await Task.Delay(4000);
                }
                else
                {
                    Console.WriteLine($"  ✅ No loading indicators detected");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Could not check loading indicators: {ex.Message}");
            }
            
            Console.WriteLine($"  ✅ Content loading wait complete for {areaName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Error waiting for content load for {areaName}: {ex.Message}");
            Console.WriteLine($"  🔄 Using fallback wait period...");
            await Task.Delay(5000);
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
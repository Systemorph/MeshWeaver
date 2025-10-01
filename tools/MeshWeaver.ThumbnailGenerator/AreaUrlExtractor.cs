using Microsoft.Playwright;

namespace MeshWeaver.ThumbnailGenerator;

public static class AreaUrlExtractor
{
    // Uses Playwright to wait for Blazor SignalR content to load, then extracts all area URLs
    public static async Task<List<string>> GetAreaUrlsAsync(string catalogUrl)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var page = await browser.NewPageAsync();

        try
        {
            Console.WriteLine($"Navigating to {catalogUrl}...");
            await page.GotoAsync(catalogUrl);

            // Handle cookie banner first - this only needs to happen on catalog load
            await HandleCookieBanner(page);

            // Wait for the catalog loaded signal (custom hook in LayoutArea.razor)
            Console.WriteLine("Waiting for catalog to load...");

            try
            {
                await page.WaitForSelectorAsync("body[data-catalog-loaded='true']", new PageWaitForSelectorOptions
                {
                    Timeout = 60000 // 60 second timeout for catalog loading
                });
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Warning: Catalog loaded signal not found, continuing anyway...");
            }

            // Additional wait for card elements to ensure they're rendered
            await page.WaitForSelectorAsync("a.card", new PageWaitForSelectorOptions
            {
                Timeout = 10000 // 10 second timeout for cards
            });

            Console.WriteLine("Catalog loaded, extracting URLs...");

            // Extract href attributes specifically from card anchor tags and transform them
            var rawUrls = await page.EvaluateAsync<string[]>(@"
                Array.from(document.querySelectorAll('a.card[href]'))
                     .map(a => a.href)
                     .filter(href => href && href.trim().length > 0)
            ");

            Console.WriteLine($"Found {rawUrls.Length} card URLs, transforming to area URLs...");

            // Transform URLs to prepend "area" after hostname
            var catalogUri = new Uri(catalogUrl);
            var baseUrl = $"{catalogUri.Scheme}://{catalogUri.Authority}";

            var areaUrls = new List<string>();
            foreach (var url in rawUrls)
            {
                try
                {
                    var uri = new Uri(url);
                    // Extract the path after the hostname (e.g., "app/Northwind/AnnualReportSummary")
                    var originalPath = uri.AbsolutePath.TrimStart('/');

                    // Construct new URL with "area" prepended: "area/app/Northwind/AnnualReportSummary"
                    var areaPath = $"area/{originalPath}";
                    var areaUrl = $"{baseUrl}/{areaPath}";

                    areaUrls.Add(areaUrl);
                    Console.WriteLine($"  {url} -> {areaUrl}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Failed to transform URL {url}: {ex.Message}");
                }
            }

            return areaUrls.Distinct().ToList();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task HandleCookieBanner(IPage page)
    {
        try
        {
            Console.WriteLine("Checking for cookie banner on catalog page...");

            // Wait for the page to fully render and cookie banner to potentially appear
            await page.WaitForTimeoutAsync(3000);

            // Check if banner exists and is visible
            var cookieBannerExists = await page.EvaluateAsync<bool>(@"
                () => {
                    const banner = document.getElementById('meshweaver-cookie-banner');
                    if (!banner) return false;
                    
                    const computedStyle = window.getComputedStyle(banner);
                    return computedStyle.display !== 'none' && 
                           computedStyle.visibility !== 'hidden' &&
                           banner.offsetParent !== null;
                }
            ");

            if (!cookieBannerExists)
            {
                Console.WriteLine("Cookie banner not found or not visible");
                return;
            }

            Console.WriteLine("Cookie banner found, accepting cookies...");

            // Accept cookies programmatically (matches the CookiePolicy.razor localStorage key)
            await page.EvaluateAsync(@"
                () => {
                    localStorage.setItem('cookieConsent', 'accepted');
                    const banner = document.getElementById('meshweaver-cookie-banner');
                    if (banner) {
                        banner.style.display = 'none';
                    }
                }
            ");

            // Also try clicking the accept button if it exists
            try
            {
                await page.ClickAsync("#meshweaver-cookie-accept", new PageClickOptions { Timeout = 2000 });
            }
            catch (TimeoutException)
            {
                // Button not found or not clickable, but we already set localStorage
            }

            Console.WriteLine("✓ Cookie consent handled");
            await page.WaitForTimeoutAsync(1000); // Brief wait for any UI updates
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Cookie banner handling failed: {ex.Message}");
        }
    }
}

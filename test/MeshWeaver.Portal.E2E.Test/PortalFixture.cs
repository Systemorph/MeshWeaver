using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Shared fixture for the collaborative-markdown E2E tests. It resolves a portal to drive and a
/// Chromium browser, and mints DevLogin-authenticated browser contexts.
///
/// <para>
/// Enablement (otherwise <see cref="Available"/> is false and tests Skip):
/// <list type="bullet">
///   <item><c>E2E_BASE_URL</c> — point at a portal you already started (recommended). e.g.
///     <c>https://localhost:7122</c> for the standalone monolith.</item>
///   <item><c>E2E_LAUNCH=1</c> — launch <c>memex/Memex.Portal.Monolith</c> as a subprocess on
///     <c>http://localhost:5099</c> and tear it down afterwards.</item>
/// </list>
/// <c>E2E_USER</c> (default <c>Roland</c>) is the DevLogin person id.
/// </para>
/// </summary>
public sealed class PortalFixture : IAsyncLifetime
{
    private static readonly string User = Environment.GetEnvironmentVariable("E2E_USER") ?? "Roland";
    private const string LaunchUrl = "http://localhost:5099";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private Process? _portalProcess;

    /// <summary>The base URL of the portal under test, or null when E2E is not enabled.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>True when a portal + browser are available, i.e. the tests should run.</summary>
    public bool Available => BaseUrl is not null && _browser is not null;

    /// <summary>The reason the suite is skipped, when <see cref="Available"/> is false.</summary>
    public string SkipReason { get; private set; } =
        "E2E disabled — set E2E_BASE_URL=<portal url> (or E2E_LAUNCH=1) to run the browser tests.";

    public async ValueTask InitializeAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL");
        var launch = Environment.GetEnvironmentVariable("E2E_LAUNCH") == "1";
        if (string.IsNullOrWhiteSpace(baseUrl) && !launch)
            return;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = LaunchUrl;
            _portalProcess = TryLaunchPortal(baseUrl);
            if (_portalProcess is null)
            {
                SkipReason = "E2E_LAUNCH=1 but the monolith could not be started (repo root not found).";
                return;
            }
        }

        if (!await WaitForPortalAsync(baseUrl, TimeSpan.FromSeconds(_portalProcess is null ? 30 : 180)))
        {
            SkipReason = $"E2E portal at {baseUrl} did not become reachable.";
            return;
        }

        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (Exception ex)
        {
            // Browser not installed → playwright.ps1 install chromium. Skip rather than fail.
            SkipReason = $"Chromium could not launch ({ex.Message}). Run: pwsh " +
                         "test/MeshWeaver.Portal.E2E.Test/bin/Debug/net10.0/playwright.ps1 install chromium";
            return;
        }

        BaseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Creates a fresh browser context authenticated as the DevLogin user. The auth cookie is minted
    /// by POSTing the <c>/dev/signin</c> form through the context's request API (shared cookie jar),
    /// so subsequent page navigations in the context are logged in.
    /// </summary>
    public async Task<IBrowserContext> NewAuthenticatedContextAsync()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
        var response = await context.APIRequest.PostAsync($"{BaseUrl}/dev/signin", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["content-type"] = "application/x-www-form-urlencoded" },
            Data = $"personId={Uri.EscapeDataString(User)}&returnUrl=%2F",
            MaxRedirects = 0
        });
        // Success is a 302 redirect with the auth cookie. A 4xx means the dev user does not exist —
        // set E2E_USER to a valid User node id.
        if ((int)response.Status >= 400)
            throw new InvalidOperationException(
                $"DevLogin for user '{User}' failed ({response.Status}). Set E2E_USER to a valid User node id.");
        return context;
    }

    private static Process? TryLaunchPortal(string url)
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;

        var psi = new ProcessStartInfo("dotnet",
            "run --project memex/Memex.Portal.Monolith --no-launch-profile")
        {
            WorkingDirectory = root,
            UseShellExecute = false
        };
        psi.Environment["ASPNETCORE_URLS"] = url;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        return Process.Start(psi);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static async Task<bool> WaitForPortalAsync(string baseUrl, TimeSpan timeout)
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false
        })
        { Timeout = TimeSpan.FromSeconds(5) };

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await http.GetAsync(baseUrl);
                return true; // any HTTP response means the server is up
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();

        if (_portalProcess is { HasExited: false })
        {
            try { _portalProcess.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
        _portalProcess?.Dispose();
    }
}

/// <summary>xUnit collection so the single portal/browser fixture is shared across the E2E tests.</summary>
[CollectionDefinition("portal-e2e")]
public sealed class PortalCollection : ICollectionFixture<PortalFixture>;

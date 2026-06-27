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

    /// <summary>
    /// The partition the DevLogin user can WRITE to — their own home (the onboarded id is the
    /// lower-cased user). Tests seed writable docs here instead of read-only static partitions (Doc).
    /// </summary>
    public string UserPartition => User.ToLowerInvariant();

    /// <summary>
    /// The DevLogin user's id EXACTLY as the User node / circuit identity carries it (the personId,
    /// not lower-cased) — this is the user's <c>ObjectId</c> / home partition. The per-user registry
    /// namespaces are <c>{UserId}/Skill</c>, <c>{UserId}/Agent</c>, … and an AccessAssignment granting
    /// this user a role uses <c>accessObject = UserId</c>. (Distinct from <see cref="UserPartition"/>,
    /// which is the lower-cased schema name.)
    /// </summary>
    public string UserId => User;

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

        // E2E_HEADED=1 shows the browser (with a slow-mo so you can watch); default is headless.
        var headed = Environment.GetEnvironmentVariable("E2E_HEADED") == "1";
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = !headed,
                SlowMo = headed ? 300 : 0
            });
        }
        catch (Exception ex)
        {
            // Browser not installed → playwright.ps1 install chromium. Skip rather than fail.
            SkipReason = $"Chromium could not launch ({ex.Message}). Run: pwsh " +
                         "test/MeshWeaver.Portal.E2E.Test/bin/Debug/net10.0/playwright.ps1 install chromium";
            return;
        }

        // Record a video of every test into TestResults/videos so a run leaves a watchable artifact.
        VideoDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "videos");
        Directory.CreateDirectory(VideoDir);
        BaseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Directory where per-test videos are written (under the test bin's TestResults).</summary>
    public string? VideoDir { get; private set; }

    /// <summary>
    /// Creates a fresh browser context authenticated as a DevLogin user (the default <see cref="User"/>,
    /// or <paramref name="personId"/> when given). The auth cookie is minted by POSTing the
    /// <c>/dev/signin</c> form through the context's request API (shared cookie jar), so subsequent page
    /// navigations in the context are logged in. DevLogin self-provisions any unknown <paramref name="personId"/>
    /// (EnableDevLogin is on for the e2e portal), so a second user (e.g. a space owner) is created on demand.
    /// </summary>
    public async Task<IBrowserContext> NewAuthenticatedContextAsync(string? personId = null)
    {
        var person = string.IsNullOrWhiteSpace(personId) ? User : personId!;
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            RecordVideoDir = VideoDir
        });

        // Success is a 302 redirect with the auth cookie. A 4xx means the dev user does not exist
        // (fail fast — bad E2E_USER). A 5xx is a transient: a freshly-rolled portal briefly 500s on
        // /dev/signin while DevLogin self-provisioning settles (the per-user partition + _Access grant).
        // Retry the 5xx a few times — this is the same cold-start readiness the WaitForPortalAsync gate
        // handles for the root URL, applied to the auth endpoint.
        IAPIResponse? response = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            response = await context.APIRequest.PostAsync($"{BaseUrl}/dev/signin", new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["content-type"] = "application/x-www-form-urlencoded" },
                Data = $"personId={Uri.EscapeDataString(person)}&returnUrl=%2F",
                MaxRedirects = 0
            });
            if ((int)response.Status < 500)
                break;
            await Task.Delay(2000);
        }
        if (response is null || (int)response.Status >= 400)
            throw new InvalidOperationException(
                $"DevLogin for user '{person}' failed ({response?.Status}). " +
                "A 4xx means E2E_USER is not a valid User node id; a persistent 5xx means the portal is unhealthy.");
        return context;
    }

    /// <summary>
    /// Mints an API bearer token for the DevLogin user (cookie-authorized <c>POST /api/tokens</c>),
    /// used to seed mesh content the UI can't create itself (e.g. a tracked change, which has no GUI
    /// creation path — only the AI suggests edits).
    /// </summary>
    public async Task<string> MintTokenAsync(IBrowserContext context)
    {
        var resp = await context.APIRequest.PostAsync($"{BaseUrl}/api/tokens", new APIRequestContextOptions
        {
            DataObject = new { Label = "e2e", ExpiresInDays = 1 }
        });
        if ((int)resp.Status >= 400)
            throw new InvalidOperationException($"Minting an API token failed ({resp.Status}).");
        var json = await resp.JsonAsync();
        return json!.Value.GetProperty("rawToken").GetString()!;
    }

    /// <summary>Creates a mesh node via <c>POST /api/mesh/create</c> (Bearer auth).</summary>
    public async Task CreateNodeAsync(IBrowserContext context, string token, string nodeJson)
    {
        var resp = await context.APIRequest.PostAsync($"{BaseUrl}/api/mesh/create", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new { Node = nodeJson }
        });
        var body = await resp.TextAsync();
        if ((int)resp.Status >= 400
            || body.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Error creating", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Seeding node failed ({resp.Status}): {body}");
    }

    /// <summary>
    /// Reads a node by path via <c>POST /api/mesh/get</c> (Bearer auth) and returns true when the node
    /// is readable by the token's user. The mesh API returns an <c>"Error: …"</c> sentinel (not a 4xx)
    /// when the path is missing or RLS denies it, so we branch on the body. Used to poll until an
    /// access grant has propagated (partition_access sync is eventually consistent) BEFORE driving the UI.
    /// </summary>
    public async Task<bool> CanReadNodeAsync(IBrowserContext context, string token, string path)
    {
        var resp = await context.APIRequest.PostAsync($"{BaseUrl}/api/mesh/get", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new { Path = path }
        });
        if ((int)resp.Status >= 400)
            return false;
        var body = await resp.TextAsync();
        // A readable node serializes its JSON (contains its path); a denial/miss is an "Error:"/"not found" string.
        return body.Contains($"\"{path}\"", StringComparison.Ordinal)
               && !body.StartsWith("\"Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Polls <see cref="CanReadNodeAsync"/> until it returns true or the timeout elapses.</summary>
    public async Task<bool> WaitUntilReadableAsync(IBrowserContext context, string token, string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReadNodeAsync(context, token, path))
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Applies an RFC 7396 JSON-merge patch to a node via <c>POST /api/mesh/patch</c> (Bearer auth).
    /// <paramref name="fieldsJson"/> is the merge document, e.g. <c>{"content":{"harness":"MeshWeaver"}}</c>.
    /// </summary>
    /// <summary>Deletes a node via <c>POST /api/mesh/delete</c> (Bearer auth). Missing-node is tolerated.</summary>
    public async Task DeleteNodeAsync(IBrowserContext context, string token, string path)
    {
        await context.APIRequest.PostAsync($"{BaseUrl}/api/mesh/delete", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new { Paths = path }
        });
        // Best-effort: a not-found / already-deleted node is fine for test setup.
    }

    public async Task PatchNodeAsync(IBrowserContext context, string token, string path, string fieldsJson)
    {
        var resp = await context.APIRequest.PostAsync($"{BaseUrl}/api/mesh/patch", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new { Path = path, Fields = fieldsJson }
        });
        var body = await resp.TextAsync();
        if ((int)resp.Status >= 400 || body.StartsWith("\"Error", StringComparison.OrdinalIgnoreCase)
            || body.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Patching '{path}' failed ({resp.Status}): {body}");
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

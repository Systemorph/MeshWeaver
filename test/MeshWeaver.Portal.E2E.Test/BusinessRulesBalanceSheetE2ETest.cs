using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E.Test;

/// <summary>
/// The full business-rules PROCESS, end to end, in a real browser: seed the PensionFund
/// balance-sheet sample (NodeType definitions + their Source code — the IScope&lt;,&gt;
/// business rules included — dimension and fact nodes, and the report instance) through the
/// public mesh API, let the portal DYNAMICALLY compile the NodeType (Roslyn + the built-in
/// scope generator, no NuGet feed), then open the report page and assert the SCOPE-COMPUTED
/// numbers render in the GUI: both years balance (1'060.0 / 1'142.0) and the funding ratio
/// evaluates (≈109.8% / ≈112.4%). A generator regression, a compile-path defect (e.g. the
/// legacy-#r feed round-trip), a scope-fold bug, or a rendering break all fail HERE — in the
/// same shape a user would see it.
///
/// <para>The in-proc twin is <c>PensionFundBalanceSheetTest</c> (kernel-level, no browser);
/// this test covers the remaining hops: REST create → per-node hub activation → dynamic
/// compile on the deployed image → Blazor render over SignalR.</para>
/// </summary>
[Collection("portal-e2e")]
public class BusinessRulesBalanceSheetE2ETest(PortalFixture fixture, ITestOutputHelper output)
{
    /// <summary>Repo-relative sample root (bin/Release/net10.0 → repo root is 5 levels up).</summary>
    private static readonly string SampleRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "samples", "Graph", "Data", "PensionFund"));

    [Fact(Timeout = 600_000)]
    public async Task BalanceSheet_ScopeComputedNumbers_RenderInBrowser()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        Assert.SkipUnless(Directory.Exists(SampleRoot),
            $"PensionFund sample not found at {SampleRoot} — run from the repo tree.");

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // ── 1. Seed the sample verbatim: type definitions first, then their Source code
        //       (the business rules), then dimension/fact instances. The report instance
        //       comes LAST, after the NodeType compile settles — its content declares
        //       $type: BalanceSheetReport, a type that only EXISTS once the compile is done.
        foreach (var typeJson in new[]
                 { "Year.json", "Currency.json", "Position.json", "BalanceSheetEntry.json", "BalanceSheet.json" })
            await SeedFileAsync(context, token, Path.Combine(SampleRoot, typeJson));

        foreach (var typeDir in new[] { "Year", "Currency", "Position", "BalanceSheetEntry", "BalanceSheet" })
        {
            var sourceDir = Path.Combine(SampleRoot, typeDir, "Source");
            if (!Directory.Exists(sourceDir))
                continue;
            foreach (var cs in Directory.GetFiles(sourceDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
                await SeedCodeAsync(context, token, typeDir, cs);
        }

        foreach (var typeDir in new[] { "Year", "Currency", "Position", "BalanceSheetEntry" })
        foreach (var instance in Directory.GetFiles(Path.Combine(SampleRoot, typeDir), "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
            await SeedFileAsync(context, token, instance);

        // ── 2. Wait for the BalanceSheet NodeType's dynamic compile (5 Source files +
        //       the built-in scope generator) to SETTLE before creating the typed instance.
        var settled = await WaitForCompileAsync(context, token, "PensionFund/BalanceSheet",
            TimeSpan.FromSeconds(180));
        Assert.True(settled, "the BalanceSheet NodeType compile must settle Ok — " +
                             "a compile error here means the scope generator / compile path broke");

        await SeedFileAsync(context, token, Path.Combine(SampleRoot, "Statement.json"));
        Assert.True(await fixture.WaitUntilReadableAsync(context, token, "PensionFund/Statement",
            TimeSpan.FromSeconds(60)), "the seeded Statement instance must become readable");

        // ── 3. Drive the GUI: the report page must render the SCOPE-COMPUTED numbers.
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 1000);
        await page.GotoAsync($"{fixture.BaseUrl}/PensionFund/Statement",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // First activation of the per-node hub renders the statement; the funding-ratio row
        // is the deepest computation (a Ratio position over Sum positions with negative
        // weights) — when IT shows, the whole scope graph evaluated.
        await page.GetByText("FundingRatio").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 180_000
        });

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = "/tmp/business-rules-balance-sheet.png",
            FullPage = true
        });

        var body = await page.Locator("body").InnerTextAsync();
        output.WriteLine(body.Length > 4000 ? body[..4000] : body);

        // Dimension members render as rows …
        Assert.Contains("Cash", body);
        Assert.Contains("PensionersCapital", body);
        // … and the computed positions fold through the PositionValue scopes:
        // totals per year and the funding ratio (separator-tolerant: 1'060.0 / 1,060.0).
        AssertHasNumber(body, @"1['’,]?060[.,]0", "2024 total must be 1'060.0");
        AssertHasNumber(body, @"1['’,]?142[.,]0", "2025 total must be 1'142.0");
        AssertHasNumber(body, @"109[.,]8", "2024 funding ratio must be ≈109.8%");
        AssertHasNumber(body, @"112[.,]4", "2025 funding ratio must be ≈112.4%");
    }

    private static void AssertHasNumber(string body, string pattern, string because)
        => Assert.True(System.Text.RegularExpressions.Regex.IsMatch(body, pattern),
            $"{because} — pattern '{pattern}' not found in rendered page");

    /// <summary>Seeds a sample MeshNode JSON file as-is; a node that already exists (rerun
    /// against a kept e2e DB) is fine — the sample is immutable.</summary>
    private async Task SeedFileAsync(IBrowserContext context, string token, string path)
    {
        var json = await File.ReadAllTextAsync(path);
        try
        {
            await fixture.CreateNodeAsync(context, token, json);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"seed {Path.GetFileName(path)}: already exists — reusing");
        }
    }

    /// <summary>Seeds a Source .cs file as a Code node under {type}/Source — the exact shape
    /// the filesystem persistence gives sample sources (CSharpFileParser → CodeConfiguration).</summary>
    private async Task SeedCodeAsync(IBrowserContext context, string token, string typeDir, string csPath)
    {
        var id = Path.GetFileNameWithoutExtension(csPath);
        var code = await File.ReadAllTextAsync(csPath);
        var node = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["namespace"] = $"PensionFund/{typeDir}/Source",
            ["name"] = id,
            ["nodeType"] = "Code",
            ["state"] = "Active",
            ["isPersistent"] = true,
            ["content"] = new Dictionary<string, object?>
            {
                ["$type"] = "CodeConfiguration",
                ["code"] = code,
                ["language"] = "csharp"
            }
        });
        try
        {
            await fixture.CreateNodeAsync(context, token, node);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"seed {typeDir}/Source/{id}: already exists — reusing");
        }
    }

    /// <summary>Polls the NodeType node via the mesh get API until its compilationStatus is Ok.</summary>
    private async Task<bool> WaitForCompileAsync(IBrowserContext context, string token, string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var resp = await context.APIRequest.PostAsync($"{fixture.BaseUrl}/api/mesh/get",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
                    DataObject = new { Path = path }
                });
            last = await resp.TextAsync();
            if (last.Contains("\"compilationStatus\"", StringComparison.OrdinalIgnoreCase)
                && last.Contains("Ok", StringComparison.Ordinal))
                return true;
            if (last.Contains("\"Error\"", StringComparison.Ordinal)
                && last.Contains("compilationError", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine($"compile settled at Error:\n{last[..Math.Min(last.Length, 2000)]}");
                return false;
            }
            await Task.Delay(2000);
        }
        output.WriteLine($"compile poll timed out; last get:\n{last[..Math.Min(last.Length, 2000)]}");
        return false;
    }
}

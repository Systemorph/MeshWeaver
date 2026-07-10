using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end for the MCP <c>github_sync</c> tool through the REAL portal's <c>/mcp</c> endpoint —
/// exactly as a headless MCP client (Claude Code / GitHub Copilot) would reach it: the streamable-HTTP
/// transport, a freshly minted <c>ApiToken</c> as <c>Authorization: Bearer</c>, and a <c>tools/call</c>.
///
/// <para>The tool FIRES the GitHub op as a mesh Activity under the token's identity and returns the
/// activity HANDLE immediately (it never blocks on the GitHub round-trip). This test asserts that
/// headless contract without needing real GitHub creds: for a Space with no <c>_GitSync</c> config the
/// op still creates its Activity node and returns a <c>Started</c> envelope with an activityPath; the
/// activity then reaches a terminal Status (Failed, "No repository URL is set" — the correct outcome
/// with no repo configured). It proves the MCP tool is reachable, runs under the caller identity, and
/// drives the activity lifecycle end-to-end through the running portal.</para>
/// </summary>
[Collection("portal-e2e")]
public class McpGitHubSyncE2ETest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task GitHubSync_OverMcp_FiresActivity_AndReturnsHandle()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        var space = "mcpghe2e";

        // ── Seed: a Space the token user owns (its creator = the token identity) ──────────────────
        try
        {
            await fixture.CreateNodeAsync(context, token, $$"""
                {
                  "id": "{{space}}",
                  "name": "MCP GitHub Sync E2E",
                  "nodeType": "Space",
                  "content": { "$type": "Space", "name": "MCP GitHub Sync E2E" }
                }
                """);
        }
        catch (InvalidOperationException)
        {
            // Persisted from a prior run — fine.
        }

        // ── Connect the MCP client to the portal's /mcp endpoint with the Bearer token ────────────
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(new Uri(fixture.BaseUrl!.TrimEnd('/') + "/"), "mcp"),
            Name = "github_sync e2e",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        await using var client = await McpClient.CreateAsync(transport);

        // The tool must be advertised on the server.
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "github_sync");

        // ── Call github_sync (commit) headlessly ─────────────────────────────────────────────────
        var result = await client.CallToolAsync(
            "github_sync",
            new Dictionary<string, object?> { ["space"] = space, ["op"] = "commit" });

        var text = ExtractText(result);
        Assert.False(string.IsNullOrWhiteSpace(text), "github_sync returned no text content.");
        Assert.DoesNotContain("Error:", text);

        using var doc = JsonDocument.Parse(text);
        Assert.Equal("Started", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("commit", doc.RootElement.GetProperty("op").GetString());
        var activityPath = doc.RootElement.GetProperty("activityPath").GetString();
        Assert.False(string.IsNullOrEmpty(activityPath), "github_sync did not return an activity handle.");
        Assert.StartsWith($"{space}/_Activity/", activityPath);

        // ── The Activity node materializes and reaches a terminal Status through the real portal ──
        // (Failed, because no repository is configured — the CORRECT outcome; the point is that the
        //  headless MCP trigger created + drove the activity under the token identity.)
        var readable = await fixture.WaitUntilReadableAsync(context, token, activityPath!, TimeSpan.FromSeconds(60));
        Assert.True(readable, $"Activity node {activityPath} never became readable.");

        var terminal = await WaitForTerminalStatusAsync(context, token, activityPath!, TimeSpan.FromSeconds(90));
        Assert.True(terminal is "Succeeded" or "Failed" or "Cancelled",
            $"Activity {activityPath} did not reach a terminal Status (was '{terminal}').");
    }

    /// <summary>Extracts the text payload from a CallToolResult's content blocks.</summary>
    private static string ExtractText(CallToolResult result)
    {
        foreach (var block in result.Content)
            if (block is TextContentBlock { Text: { Length: > 0 } t })
                return t;
        return "";
    }

    /// <summary>Polls the activity node via the mesh REST get until its ActivityLog Status is terminal.</summary>
    private static async Task<string?> WaitForTerminalStatusAsync(
        Microsoft.Playwright.IBrowserContext context, string token, string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var resp = await context.APIRequest.PostAsync($"{FixtureBaseUrl(context)}/api/mesh/get",
                new Microsoft.Playwright.APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
                    DataObject = new { Path = path },
                });
            if ((int)resp.Status < 400)
            {
                var body = await resp.TextAsync();
                var status = TryReadStatus(body);
                if (status is "Succeeded" or "Failed" or "Cancelled")
                    return status;
            }
            await Task.Delay(1000);
        }
        return null;
    }

    // The mesh get returns the node JSON (escaped) — the ActivityLog Status shows up as a "status" or
    // "Status" property in the content. Read it robustly from the raw body.
    private static string? TryReadStatus(string body)
    {
        foreach (var key in new[] { "\"status\":\"", "\"Status\":\"" })
        {
            var i = body.IndexOf(key, StringComparison.Ordinal);
            while (i >= 0)
            {
                var start = i + key.Length;
                var end = body.IndexOf('"', start);
                if (end > start)
                {
                    var v = body[start..end];
                    if (v is "Succeeded" or "Failed" or "Cancelled" or "Running")
                        return v;
                }
                i = body.IndexOf(key, start, StringComparison.Ordinal);
            }
        }
        return null;
    }

    private static string FixtureBaseUrl(Microsoft.Playwright.IBrowserContext _)
        => Environment.GetEnvironmentVariable("E2E_BASE_URL")?.TrimEnd('/')
           ?? "https://e2e.memex.localhost:8444";
}

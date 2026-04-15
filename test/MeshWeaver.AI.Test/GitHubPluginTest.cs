#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for GitHubPlugin tool surface and API integration.
/// API tests are conditionally skipped when GITHUB_TOKEN is not set.
/// </summary>
public class GitHubPluginTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public GitHubPluginTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {

                services.AddGitHubPlugin();
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    #region Test Infrastructure

    internal class TestHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpResponseMessage ConfiguredResponse { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return ConfiguredResponse;
        }
    }

    private static (GitHubPlugin plugin, TestHttpMessageHandler handler) CreateTestPlugin(
        string pat = "test-pat", string? owner = "TestOwner", string? repo = "TestRepo")
    {
        var handler = new TestHttpMessageHandler();
        var client = new HttpClient(handler);
        var plugin = new GitHubPlugin(client, Options.Create(new GitHubConfiguration
        {
            PersonalAccessToken = pat, DefaultOwner = owner, DefaultRepo = repo
        }), NullLogger<GitHubPlugin>.Instance);
        return (plugin, handler);
    }

    #endregion

    #region Group 1: Tool Registration (existing)

    /// <summary>
    /// Verifies that GitHubPlugin is registered and exposes the expected tools.
    /// </summary>
    [Fact]
    public void CreateTools_ReturnsExpectedTools()
    {
        var plugin = Mesh.ServiceProvider.GetRequiredService<IEnumerable<IAgentPlugin>>()
            .First(p => p.Name == "GitHub");
        var tools = plugin.CreateTools();
        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToList();

        Output.WriteLine($"GitHub tools: {string.Join(", ", names)}");

        names.Should().Contain("CreateIssue");
        names.Should().Contain("GetIssue");
        names.Should().Contain("ListIssues");
        names.Should().Contain("UpdateIssue");
        names.Should().HaveCount(4);
    }

    #endregion

    #region Group 1: Request Building (via TestHttpMessageHandler)

    [Fact]
    public async Task CreateIssue_SetsCorrectHeaders()
    {
        var (plugin, handler) = CreateTestPlugin(pat: "my-secret-pat");

        // Configure a valid response so the plugin can parse it
        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/TestOwner/TestRepo/issues/1",
                number = 1,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.CreateIssue(null, null, "Test", "Body");

        var req = handler.LastRequest!;
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization.Parameter.Should().Be("my-secret-pat");
        req.Headers.UserAgent.ToString().Should().Contain("MeshWeaver");
        req.Headers.Accept.Should().Contain(a => a.MediaType == "application/vnd.github+json");
        req.Headers.GetValues("X-GitHub-Api-Version").Should().Contain("2022-11-28");
        req.RequestUri!.ToString().Should().Be("https://api.github.com/repos/TestOwner/TestRepo/issues");
    }

    [Fact]
    public async Task CreateIssue_WithPayload_SerializesJson()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/TestOwner/TestRepo/issues/1",
                number = 1,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.CreateIssue(null, null, "My Title", "My Body");

        handler.LastRequestBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("title").GetString().Should().Be("My Title");
        doc.RootElement.GetProperty("body").GetString().Should().Be("My Body");
        handler.LastRequest!.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    #endregion

    #region Group 2: Error Paths

    /// <summary>
    /// Verifies that all methods return a configuration error when PAT is not set.
    /// </summary>
    [Fact]
    public async Task AllMethods_WithoutPAT_ReturnConfigError()
    {
        var (plugin, _) = CreateTestPlugin(pat: "");

        var createResult = await plugin.CreateIssue(null, null, "Test", "Body");
        var getResult = await plugin.GetIssue(null, null, 1);
        var listResult = await plugin.ListIssues(null, null);
        var updateResult = await plugin.UpdateIssue(null, null, 1, state: "closed");

        createResult.Should().Contain("not configured");
        getResult.Should().Contain("not configured");
        listResult.Should().Contain("not configured");
        updateResult.Should().Contain("not configured");
    }

    [Fact]
    public async Task CreateIssue_WithoutOwnerOrRepo_ReturnsError()
    {
        var (plugin, _) = CreateTestPlugin(owner: null, repo: null);

        var result = await plugin.CreateIssue(null, null, "Test", "Body");

        result.Should().Contain("owner and repo are required");
    }

    [Fact]
    public async Task AllMethods_WithInvalidOwner_ReturnValidationError()
    {
        var (plugin, _) = CreateTestPlugin(owner: "evil/../..", repo: "TestRepo");

        var createResult = await plugin.CreateIssue(null, null, "Test", "Body");
        var getResult = await plugin.GetIssue(null, null, 1);
        var listResult = await plugin.ListIssues(null, null);
        var updateResult = await plugin.UpdateIssue(null, null, 1, state: "closed");

        createResult.Should().Contain("alphanumeric");
        getResult.Should().Contain("alphanumeric");
        listResult.Should().Contain("alphanumeric");
        updateResult.Should().Contain("alphanumeric");
    }

    [Fact]
    public async Task AllMethods_WithInvalidRepo_ReturnValidationError()
    {
        var (plugin, _) = CreateTestPlugin(owner: "TestOwner", repo: "repo/../../etc");

        var createResult = await plugin.CreateIssue(null, null, "Test", "Body");
        var getResult = await plugin.GetIssue(null, null, 1);
        var listResult = await plugin.ListIssues(null, null);
        var updateResult = await plugin.UpdateIssue(null, null, 1, state: "closed");

        createResult.Should().Contain("alphanumeric");
        getResult.Should().Contain("alphanumeric");
        listResult.Should().Contain("alphanumeric");
        updateResult.Should().Contain("alphanumeric");
    }

    [Fact]
    public async Task CreateIssue_WithExplicitInvalidOwner_ReturnValidationError()
    {
        var (plugin, _) = CreateTestPlugin();

        var result = await plugin.CreateIssue("evil/../..", "TestRepo", "Test", "Body");

        result.Should().Contain("alphanumeric");
    }

    [Fact]
    public async Task CreateIssue_WithValidHyphensDotsUnderscores_Succeeds()
    {
        var (plugin, handler) = CreateTestPlugin(owner: "my-org.test_1", repo: "my-repo.v2_beta");

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/my-org.test_1/my-repo.v2_beta/issues/1",
                number = 1,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        var result = await plugin.CreateIssue(null, null, "Title", "Body");

        result.Should().NotContain("Error");
        result.Should().Contain("github.com");
    }

    #endregion

    #region Group 3: HTTP Behavior with TestHttpMessageHandler

    [Fact]
    public async Task CreateIssue_UsesDefaultOwnerRepo()
    {
        var (plugin, handler) = CreateTestPlugin(owner: "MyOrg", repo: "MyRepo");

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/MyOrg/MyRepo/issues/42",
                number = 42,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.CreateIssue(null, null, "Title", "Body");

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Contain("repos/MyOrg/MyRepo/issues");
    }

    [Fact]
    public async Task CreateIssue_WithLabels_SplitsCommaString()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/TestOwner/TestRepo/issues/1",
                number = 1,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.CreateIssue(null, null, "Title", "Body", "a, b, c");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var labels = doc.RootElement.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString()).ToList();
        labels.Should().BeEquivalentTo(["a", "b", "c"]);
    }

    [Fact]
    public async Task ListIssues_ClampsPerPage()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        };

        // perPage=200 should be clamped to 100
        await plugin.ListIssues(null, null, perPage: 200);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("per_page=100");

        // Need a fresh response since the previous one gets disposed
        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        };

        // perPage=0 should be clamped to 1
        await plugin.ListIssues(null, null, perPage: 0);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("per_page=1");
    }

    [Fact]
    public async Task UpdateIssue_SendsOnlyNonNullFields()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/TestOwner/TestRepo/issues/5",
                number = 5,
                state = "closed"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.UpdateIssue(null, null, 5, state: "closed");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.TryGetProperty("state", out var stateVal).Should().BeTrue();
        stateVal.GetString().Should().Be("closed");
        doc.RootElement.TryGetProperty("title", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("body", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("labels", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateIssue_WithAllFields_SendsAll()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                html_url = "https://github.com/TestOwner/TestRepo/issues/5",
                number = 5,
                state = "open"
            }), Encoding.UTF8, "application/json")
        };

        await plugin.UpdateIssue(null, null, 5,
            state: "open", title: "New Title", body: "New Body", labels: "x,y");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement.GetProperty("state").GetString().Should().Be("open");
        doc.RootElement.GetProperty("title").GetString().Should().Be("New Title");
        doc.RootElement.GetProperty("body").GetString().Should().Be("New Body");
        var labels = doc.RootElement.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString()).ToList();
        labels.Should().BeEquivalentTo(["x", "y"]);
    }

    [Fact]
    public async Task CreateIssue_HttpErrors_ReturnFormattedMessages()
    {
        var (plugin, handler) = CreateTestPlugin();

        // 401
        handler.ConfiguredResponse = new(HttpStatusCode.Unauthorized);
        var result401 = await plugin.CreateIssue(null, null, "T", "B");
        result401.Should().Contain("401").And.Contain("Unauthorized");

        // 403
        handler.ConfiguredResponse = new(HttpStatusCode.Forbidden);
        var result403 = await plugin.CreateIssue(null, null, "T", "B");
        result403.Should().Contain("403").And.Contain("Forbidden");

        // 404
        handler.ConfiguredResponse = new(HttpStatusCode.NotFound);
        var result404 = await plugin.CreateIssue(null, null, "T", "B");
        result404.Should().Contain("404");
    }

    [Fact]
    public async Task GetIssue_ReturnsFormattedJson()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                title = "Fix login bug",
                state = "open",
                body = "Steps to reproduce...",
                html_url = "https://github.com/TestOwner/TestRepo/issues/42",
                number = 42,
                labels = new[] { new { name = "bug" }, new { name = "priority:high" } },
                created_at = "2025-01-15T10:00:00Z",
                updated_at = "2025-01-16T12:00:00Z"
            }), Encoding.UTF8, "application/json")
        };

        var result = await plugin.GetIssue(null, null, 42);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Fix login bug");
        doc.RootElement.GetProperty("state").GetString().Should().Be("open");
        doc.RootElement.GetProperty("number").GetInt32().Should().Be(42);
        doc.RootElement.GetProperty("url").GetString().Should().Contain("github.com");
        var labels = doc.RootElement.GetProperty("labels").EnumerateArray()
            .Select(l => l.GetString()).ToList();
        labels.Should().BeEquivalentTo(["bug", "priority:high"]);
    }

    [Fact]
    public async Task ListIssues_ReturnsArrayOfFormattedIssues()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new[]
            {
                new
                {
                    number = 1, title = "First", state = "open",
                    html_url = "https://github.com/TestOwner/TestRepo/issues/1",
                    labels = new[] { new { name = "bug" } }
                },
                new
                {
                    number = 2, title = "Second", state = "closed",
                    html_url = "https://github.com/TestOwner/TestRepo/issues/2",
                    labels = new[] { new { name = "none" } }
                }
            }), Encoding.UTF8, "application/json")
        };

        var result = await plugin.ListIssues(null, null);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("title").GetString().Should().Be("First");
        doc.RootElement[1].GetProperty("title").GetString().Should().Be("Second");
        doc.RootElement[1].GetProperty("state").GetString().Should().Be("closed");
    }

    [Fact]
    public async Task ListIssues_IncludesLabelsInQueryString()
    {
        var (plugin, handler) = CreateTestPlugin();

        handler.ConfiguredResponse = new(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        };

        await plugin.ListIssues(null, null, labels: "bug,feature");

        handler.LastRequest!.RequestUri!.Query.Should().Contain("labels=");
        // URL-encoded comma
        var query = handler.LastRequest.RequestUri.Query;
        query.Should().Contain("bug");
    }

    #endregion

    #region Integration Tests (conditionally skipped)

    /// <summary>
    /// Integration test: creates and closes an issue on GitHub.
    /// Skipped when GITHUB_TOKEN is not set.
    /// </summary>
    [Fact]
    public async Task CreateIssue_WithValidPAT_ReturnsIssueUrl()
    {
        var pat = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(pat))
        {
            Output.WriteLine("Skipping: GITHUB_TOKEN not set");
            return;
        }

        var loggerFactory = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var plugin = new GitHubPlugin(
            new HttpClient(),
            Options.Create(new GitHubConfiguration
            {
                PersonalAccessToken = pat,
                DefaultOwner = "Systemorph",
                DefaultRepo = "MeshWeaver"
            }),
            loggerFactory.CreateLogger<GitHubPlugin>());

        var result = await plugin.CreateIssue(
            null, null,
            "[TEST] TDD Integration Test - Safe to Close",
            "Automated test issue. Please close.",
            "test");

        Output.WriteLine($"CreateIssue result: {result}");
        result.Should().Contain("github.com");

        // Clean up: close the issue
        using var doc = JsonDocument.Parse(result);
        var number = doc.RootElement.GetProperty("number").GetInt32();
        var closeResult = await plugin.UpdateIssue(null, null, number, state: "closed");
        Output.WriteLine($"CloseIssue result: {closeResult}");
    }

    /// <summary>
    /// Integration test: reads an existing issue.
    /// Skipped when GITHUB_TOKEN is not set.
    /// </summary>
    [Fact]
    public async Task GetIssue_Integration_ReturnsIssueDetails()
    {
        var pat = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(pat))
        {
            Output.WriteLine("Skipping: GITHUB_TOKEN not set");
            return;
        }

        var loggerFactory = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var plugin = new GitHubPlugin(
            new HttpClient(),
            Options.Create(new GitHubConfiguration
            {
                PersonalAccessToken = pat,
                DefaultOwner = "Systemorph",
                DefaultRepo = "MeshWeaver"
            }),
            loggerFactory.CreateLogger<GitHubPlugin>());

        var result = await plugin.GetIssue(null, null, 1);
        Output.WriteLine($"GetIssue result: {result}");

        result.Should().NotStartWith("Error");
        using var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("title", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("state", out _).Should().BeTrue();
    }

    #endregion
}

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    /// <summary>
    /// Verifies that tools return a configuration error when PAT is not set.
    /// </summary>
    [Fact]
    public async Task CreateIssue_WithoutPAT_ReturnsConfigError()
    {
        var plugin = Mesh.ServiceProvider.GetRequiredService<IEnumerable<IAgentPlugin>>()
            .First(p => p.Name == "GitHub") as GitHubPlugin;
        plugin.Should().NotBeNull();

        var result = await plugin!.CreateIssue(null, null, "Test", "Body");
        result.Should().Contain("not configured");
    }

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
    public async Task GetIssue_ReturnsIssueDetails()
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
}

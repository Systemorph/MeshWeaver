using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Documentation;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Verifies that all built-in agent markdown files and documentation parse
/// without illegal references. Catches issues where placeholder paths like
/// "@path" or "@{address}" are used that the AI agent or renderer would
/// try to resolve literally.
/// </summary>
public class AgentMarkdownParsingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddDocumentation();

    /// <summary>
    /// Verifies all built-in agent nodes load without exceptions.
    /// </summary>
    [Fact]
    public void BuiltInAgentProvider_LoadsAllNodes()
    {
        var provider = new BuiltInAgentProvider();
        var nodes = provider.GetStaticNodes().ToList();

        nodes.Should().NotBeEmpty("should have at least the built-in agents");
        Output.WriteLine($"Loaded {nodes.Count} agent nodes:");
        foreach (var node in nodes)
            Output.WriteLine($"  {node.Path}: {node.NodeType} - {node.Name}");

        // Verify expected agents exist
        nodes.Should().Contain(n => n.Path == "Agent/Orchestrator", "Orchestrator agent should exist");
        nodes.Should().Contain(n => n.Path == "Agent/Worker", "Worker agent should exist");
        nodes.Should().Contain(n => n.Path == "Agent/Planner", "Planner agent should exist");
        nodes.Should().Contain(n => n.Path == "Agent/Researcher", "Researcher agent should exist");
        nodes.Should().Contain(n => n.Path == "Agent/ToolsReference", "ToolsReference should exist");
    }

    /// <summary>
    /// Verifies no agent instructions contain the literal "@path" placeholder
    /// which the AI agent would try to use as an actual address.
    /// </summary>
    [Fact]
    public void AgentInstructions_NoLiteralPathPlaceholders()
    {
        var provider = new BuiltInAgentProvider();
        var nodes = provider.GetStaticNodes().ToList();

        var badPatterns = new[] { "@path/", "@path'", "@@path/", "@@path " };
        var violations = new List<string>();

        foreach (var node in nodes)
        {
            string? instructions = null;
            if (node.Content is AgentConfiguration config)
                instructions = config.Instructions;
            else if (node.Content is MeshWeaver.Markdown.MarkdownContent md)
                instructions = md.Content;

            if (string.IsNullOrEmpty(instructions))
                continue;

            foreach (var pattern in badPatterns)
            {
                if (instructions.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{node.Path}: contains '{pattern}'");
                }
            }
        }

        if (violations.Count > 0)
        {
            Output.WriteLine("Violations found:");
            foreach (var v in violations)
                Output.WriteLine($"  - {v}");
        }

        violations.Should().BeEmpty(
            "agent instructions must not contain literal '@path' — " +
            "the AI agent will try to use it as an actual address. " +
            "Use a real example node path instead.");
    }

    /// <summary>
    /// Verifies that @@ inline references in agent markdown point to nodes
    /// that actually exist (either as static nodes or in persistence).
    /// </summary>
    [Fact]
    public async Task AgentInstructions_InlineReferences_PointToExistingNodes()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var provider = new BuiltInAgentProvider();
        var allStaticNodes = provider.GetStaticNodes().ToList();

        // Also get documentation static nodes
        var docProvider = Mesh.ServiceProvider.GetServices<IStaticNodeProvider>();
        var allNodes = allStaticNodes
            .Concat(docProvider.SelectMany(p => p.GetStaticNodes()))
            .ToList();

        var allNodePaths = allNodes.Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find all @@Reference patterns in agent instructions (at start of line)
        var inlineRefRegex = new Regex(@"^@@(\S+)", RegexOptions.Multiline);
        var missingRefs = new List<string>();

        foreach (var node in allStaticNodes)
        {
            string? instructions = null;
            if (node.Content is AgentConfiguration config)
                instructions = config.Instructions;
            else if (node.Content is MeshWeaver.Markdown.MarkdownContent md)
                instructions = md.Content;

            if (string.IsNullOrEmpty(instructions))
                continue;

            foreach (Match match in inlineRefRegex.Matches(instructions))
            {
                var refPath = match.Groups[1].Value;
                Output.WriteLine($"  {node.Path}: @@{refPath}");

                // Check if the referenced path exists as a static node
                if (!allNodePaths.Contains(refPath))
                {
                    // Also check if it's a content reference (contains ':')
                    if (!refPath.Contains(':'))
                    {
                        // Try querying persistence
                        var found = await MeshQuery.QueryAsync<MeshNode>($"path:{refPath}")
                            .FirstOrDefaultAsync(ct);
                        if (found == null)
                            missingRefs.Add($"{node.Path}: @@{refPath} — node not found");
                    }
                    // Content references (e.g., Doc/AI/content:inline-example.md) are harder
                    // to validate statically — skip for now
                }
            }
        }

        if (missingRefs.Count > 0)
        {
            Output.WriteLine("\nMissing references:");
            foreach (var r in missingRefs)
                Output.WriteLine($"  - {r}");
        }

        missingRefs.Should().BeEmpty(
            "@@ inline references must point to existing nodes. " +
            "Missing nodes will cause rendering errors.");
    }
}

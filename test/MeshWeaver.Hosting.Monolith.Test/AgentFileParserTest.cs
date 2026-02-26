using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Unit tests for AgentFileParser - parsing and serialization of agent markdown files with YAML front matter.
/// </summary>
public class AgentFileParserTest
{
    private readonly AgentFileParser _parser = new();

    #region Parse Tests

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_ValidAgentMarkdown_ReturnsAgentConfiguration()
    {
        // Arrange
        var content = """
            ---
            nodeType: Agent
            name: Test Agent
            description: A test agent
            icon: Bot
            category: Testing
            ---

            You are a test agent. Do testing things.
            """;

        // Act
        var node = await _parser.ParseAsync("/test/TestAgent.md", content, "test/TestAgent.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("TestAgent");
        node.Namespace.Should().Be("test");
        node.NodeType.Should().Be("Agent");
        node.Name.Should().Be("Test Agent");
        node.Icon.Should().Be("Bot");
        node.Category.Should().Be("Testing");

        var agentConfig = node.Content.Should().BeOfType<AgentConfiguration>().Subject;
        agentConfig.Id.Should().Be("TestAgent");
        agentConfig.DisplayName.Should().Be("Test Agent");
        agentConfig.Description.Should().Be("A test agent");
        agentConfig.Instructions.Should().Be("You are a test agent. Do testing things.");
    }

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_WithDelegations_ParsesDelegationsList()
    {
        // Arrange
        var content = """
            ---
            nodeType: Agent
            name: Navigator
            delegations:
              - agentPath: Planner
                instructions: Handle planning tasks
              - agentPath: Executor
                instructions: Execute actions
            ---

            You are Navigator.
            """;

        // Act
        var node = await _parser.ParseAsync("/Navigator.md", content, "Navigator.md");

        // Assert
        node.Should().NotBeNull();
        var agentConfig = node!.Content.Should().BeOfType<AgentConfiguration>().Subject;
        agentConfig.Delegations.Should().HaveCount(2);
        agentConfig.Delegations![0].AgentPath.Should().Be("Planner");
        agentConfig.Delegations[0].Instructions.Should().Be("Handle planning tasks");
        agentConfig.Delegations[1].AgentPath.Should().Be("Executor");
        agentConfig.Delegations[1].Instructions.Should().Be("Execute actions");
    }

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_InstructionsInMarkdownBody_ExtractsToInstructions()
    {
        // Arrange
        var content = """
            ---
            nodeType: Agent
            name: Research Agent
            ---

            # Research Agent

            You are a research agent. Your tasks:
            - Search for information
            - Summarize findings
            - Report results

            ## Guidelines

            Be thorough and accurate.
            """;

        // Act
        var node = await _parser.ParseAsync("/Research.md", content, "Research.md");

        // Assert
        node.Should().NotBeNull();
        var agentConfig = node!.Content.Should().BeOfType<AgentConfiguration>().Subject;
        agentConfig.Instructions.Should().Contain("# Research Agent");
        agentConfig.Instructions.Should().Contain("- Search for information");
        agentConfig.Instructions.Should().Contain("## Guidelines");
        agentConfig.Instructions.Should().Contain("Be thorough and accurate.");
    }

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_WithAllProperties_ParsesAllProperties()
    {
        // Arrange
        var content = """
            ---
            nodeType: Agent
            name: Full Agent
            description: Agent with all properties
            icon: Star
            category: Full
            groupName: TestGroup
            isDefault: true
            exposedInNavigator: true
            contextMatchPattern: address=like=*Test*
            preferredModel: gpt-4
            order: 10
            ---

            Full agent instructions.
            """;

        // Act
        var node = await _parser.ParseAsync("/FullAgent.md", content, "FullAgent.md");

        // Assert
        node.Should().NotBeNull();
        var agentConfig = node!.Content.Should().BeOfType<AgentConfiguration>().Subject;
        agentConfig.GroupName.Should().Be("TestGroup");
        agentConfig.IsDefault.Should().BeTrue();
        agentConfig.ExposedInNavigator.Should().BeTrue();
        agentConfig.ContextMatchPattern.Should().Be("address=like=*Test*");
        agentConfig.PreferredModel.Should().Be("gpt-4");
        agentConfig.Order.Should().Be(10);
    }

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_NonAgentNodeType_ReturnsNull()
    {
        // Arrange
        var content = """
            ---
            nodeType: Markdown
            name: Not an Agent
            ---

            This is markdown content.
            """;

        // Act
        var node = await _parser.ParseAsync("/test.md", content, "test.md");

        // Assert
        node.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task ParseAsync_NoYamlFrontMatter_ReturnsNull()
    {
        // Arrange
        var content = """
            # Just Markdown

            No YAML front matter here.
            """;

        // Act
        var node = await _parser.ParseAsync("/test.md", content, "test.md");

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region Serialize Tests

    [Fact(Timeout = 10000)]
    public async Task SerializeAsync_AgentNode_ProducesValidMarkdown()
    {
        // Arrange
        var agentConfig = new AgentConfiguration
        {
            Id = "TestAgent",
            DisplayName = "Test Agent",
            Description = "A test agent",
            Instructions = "You are a test agent.",
            Icon = "Bot",
            GroupName = "Testing",
            IsDefault = false,
            ExposedInNavigator = true
        };

        var node = new MeshNode("TestAgent", "test")
        {
            NodeType = "Agent",
            Name = "Test Agent",
            Icon = "Bot",
            Category = "Agents",
            Content = agentConfig
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert
        result.Should().Contain("---");
        result.Should().Contain("nodeType: Agent");
        result.Should().Contain("name: Test Agent");
        result.Should().Contain("description: A test agent");
        result.Should().Contain("exposedInNavigator: true");
        result.Should().Contain("groupName: Testing");
        result.Should().Contain("You are a test agent.");
    }

    [Fact(Timeout = 10000)]
    public async Task SerializeAsync_WithDelegations_SerializesDelegations()
    {
        // Arrange
        var agentConfig = new AgentConfiguration
        {
            Id = "Navigator",
            DisplayName = "Navigator",
            Instructions = "Navigate requests.",
            Delegations =
            [
                new AgentDelegation { AgentPath = "Planner", Instructions = "Plan tasks" },
                new AgentDelegation { AgentPath = "Executor", Instructions = "Execute tasks" }
            ]
        };

        var node = new MeshNode("Navigator")
        {
            NodeType = "Agent",
            Name = "Navigator",
            Content = agentConfig
        };

        // Act
        var result = await _parser.SerializeAsync(node);

        // Assert
        result.Should().Contain("delegations:");
        result.Should().Contain("agentPath: Planner");
        result.Should().Contain("instructions: Plan tasks");
        result.Should().Contain("agentPath: Executor");
        result.Should().Contain("instructions: Execute tasks");
    }

    #endregion

    #region CanSerialize Tests

    [Fact(Timeout = 10000)]
    public void CanSerialize_AgentNode_ReturnsTrue()
    {
        var node = new MeshNode("test") { NodeType = "Agent" };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_AgentConfigurationContent_ReturnsTrue()
    {
        var node = new MeshNode("test")
        {
            Content = new AgentConfiguration { Id = "test" }
        };
        _parser.CanSerialize(node).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_MarkdownNode_ReturnsFalse()
    {
        var node = new MeshNode("test")
        {
            NodeType = "Markdown",
            Content = "# Some markdown"
        };
        _parser.CanSerialize(node).Should().BeFalse();
    }

    [Fact(Timeout = 10000)]
    public void CanSerialize_OtherNodeType_ReturnsFalse()
    {
        var node = new MeshNode("test")
        {
            NodeType = "Organization",
            Content = new { Id = "test" }
        };
        _parser.CanSerialize(node).Should().BeFalse();
    }

    #endregion

    #region Round-Trip Tests

    [Fact(Timeout = 10000)]
    public async Task RoundTrip_PreservesAllProperties()
    {
        // Arrange
        var originalContent = """
            ---
            nodeType: Agent
            name: Complete Agent
            description: Agent with all properties
            icon: Star
            category: Testing
            groupName: TestGroup
            isDefault: true
            exposedInNavigator: true
            contextMatchPattern: address=like=*Test*
            preferredModel: gpt-4
            order: 5
            delegations:
              - agentPath: Helper
                instructions: Help with tasks
            ---

            # Complete Agent Instructions

            This agent handles everything.

            ## Rules
            - Be helpful
            - Be accurate
            """;

        // Act - Parse then serialize
        var node = await _parser.ParseAsync("/test/CompleteAgent.md", originalContent, "test/CompleteAgent.md");
        var serialized = await _parser.SerializeAsync(node!);

        // Re-parse to verify
        var reparsed = await _parser.ParseAsync("/test/CompleteAgent.md", serialized, "test/CompleteAgent.md");

        // Assert
        reparsed.Should().NotBeNull();
        reparsed!.NodeType.Should().Be("Agent");
        reparsed.Name.Should().Be("Complete Agent");

        var agentConfig = reparsed.Content.Should().BeOfType<AgentConfiguration>().Subject;
        agentConfig.GroupName.Should().Be("TestGroup");
        agentConfig.IsDefault.Should().BeTrue();
        agentConfig.ExposedInNavigator.Should().BeTrue();
        agentConfig.ContextMatchPattern.Should().Be("address=like=*Test*");
        agentConfig.PreferredModel.Should().Be("gpt-4");
        agentConfig.Order.Should().Be(5);
        agentConfig.Delegations.Should().HaveCount(1);
        agentConfig.Delegations![0].AgentPath.Should().Be("Helper");
        agentConfig.Instructions.Should().Contain("# Complete Agent Instructions");
        agentConfig.Instructions.Should().Contain("- Be helpful");
    }

    #endregion

    #region IsAgentMarkdown Tests

    [Fact(Timeout = 10000)]
    public void IsAgentMarkdown_WithAgentNodeType_ReturnsTrue()
    {
        var content = """
            ---
            nodeType: Agent
            name: Test
            ---
            Instructions
            """;

        AgentFileParser.IsAgentMarkdown(content).Should().BeTrue();
    }

    [Fact(Timeout = 10000)]
    public void IsAgentMarkdown_WithMarkdownNodeType_ReturnsFalse()
    {
        var content = """
            ---
            nodeType: Markdown
            name: Test
            ---
            Content
            """;

        AgentFileParser.IsAgentMarkdown(content).Should().BeFalse();
    }

    [Fact(Timeout = 10000)]
    public void IsAgentMarkdown_WithNoYaml_ReturnsFalse()
    {
        var content = "# Just markdown content";

        AgentFileParser.IsAgentMarkdown(content).Should().BeFalse();
    }

    [Fact(Timeout = 10000)]
    public void IsAgentMarkdown_WithEmptyContent_ReturnsFalse()
    {
        AgentFileParser.IsAgentMarkdown("").Should().BeFalse();
        AgentFileParser.IsAgentMarkdown("   ").Should().BeFalse();
    }

    #endregion
}

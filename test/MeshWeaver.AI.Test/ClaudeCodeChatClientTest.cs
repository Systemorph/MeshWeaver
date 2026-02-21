#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using FluentAssertions;
using MeshWeaver.AI.ClaudeCode;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

public class ClaudeCodeChatClientTest
{
    private readonly ClaudeCodeChatClient _client;
    private readonly ClaudeCodeConfiguration _config;

    public ClaudeCodeChatClientTest()
    {
        _config = new ClaudeCodeConfiguration();
        _client = new ClaudeCodeChatClient(_config);
    }

    [Fact]
    public void BuildSystemPrompt_WithSystemMessages_ExtractsAndCombines()
    {
        // Arrange
        _config.SystemPrompt = "Be helpful";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are PricingAgent"),
            new(ChatRole.User, "What is the price?")
        };

        // Act
        var result = _client.BuildSystemPrompt(messages);

        // Assert
        result.Should().Be("You are PricingAgent\n\nBe helpful");
    }

    [Fact]
    public void BuildSystemPrompt_NoSystemMessages_ReturnsConfigPromptOnly()
    {
        // Arrange
        _config.SystemPrompt = "Be helpful";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello")
        };

        // Act
        var result = _client.BuildSystemPrompt(messages);

        // Assert
        result.Should().Be("Be helpful");
    }

    [Fact]
    public void BuildSystemPrompt_NoSystemMessagesNoConfig_ReturnsEmpty()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hello")
        };

        // Act
        var result = _client.BuildSystemPrompt(messages);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildSystemPrompt_MultipleSystemMessages_CombinesAll()
    {
        // Arrange
        _config.SystemPrompt = "Global instructions";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are PricingAgent"),
            new(ChatRole.System, "You specialize in insurance pricing"),
            new(ChatRole.User, "Calculate premium")
        };

        // Act
        var result = _client.BuildSystemPrompt(messages);

        // Assert
        result.Should().Be("You are PricingAgent\n\nYou specialize in insurance pricing\n\nGlobal instructions");
    }
}

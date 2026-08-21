#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the pure half of the Auto router's selection stage (#1951): the candidate filter, the
/// prompt contract, and — most load-bearing — the answer parser. The parser is the gate between
/// "whatever the engine replied" and "which model serves the round", so every tolerance
/// (code fences, prose, casing) and every refusal (invented id, garbage, empty) is pinned here.
/// A refusal is SAFE — the round keeps the tier/default floor — so when in doubt the parser
/// must refuse, never fuzzy-match.
/// </summary>
public class AutoModelRouterTest
{
    private static readonly string[] Candidates = ["DeepSeek-V4-Pro", "DeepSeek-V4-Flash", "z-ai/glm-5.2"];

    [Fact]
    public void ParsesAPlainJsonChoice()
    {
        var ok = AutoModelRouter.TryParseChoice(
            """{"model": "DeepSeek-V4-Pro", "reason": "multi-step coding task"}""",
            Candidates, out var chosen, out var reason);

        Assert.True(ok);
        Assert.Equal("DeepSeek-V4-Pro", chosen);
        Assert.Equal("multi-step coding task", reason);
    }

    [Fact]
    public void ToleratesCodeFencesAndProseAroundTheJson()
    {
        var ok = AutoModelRouter.TryParseChoice(
            "Sure! Here is my pick:\n```json\n{\"model\": \"z-ai/glm-5.2\", \"reason\": \"short chat\"}\n```\nHope that helps.",
            Candidates, out var chosen, out _);

        Assert.True(ok);
        Assert.Equal("z-ai/glm-5.2", chosen);
    }

    [Fact]
    public void MatchesCandidatesCaseInsensitively_ButReturnsTheCanonicalId()
    {
        var ok = AutoModelRouter.TryParseChoice(
            """{"model": "deepseek-v4-flash", "reason": "simple"}""",
            Candidates, out var chosen, out _);

        Assert.True(ok);
        Assert.Equal("DeepSeek-V4-Flash", chosen);
    }

    [Theory]
    [InlineData("""{"model": "gpt-9-ultra", "reason": "made up"}""")] // invented id
    [InlineData("""{"model": "DeepSeek", "reason": "partial id must not fuzzy-match"}""")]
    [InlineData("""{"reason": "no model field"}""")]
    [InlineData("""{"model": "", "reason": "empty"}""")]
    [InlineData("DeepSeek-V4-Pro")] // bare id without the JSON contract
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAnythingOutsideTheContract(string? response)
    {
        var ok = AutoModelRouter.TryParseChoice(response, Candidates, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void BuildMessages_NamesEveryCandidate_AndTruncatesTheTask()
    {
        var candidates = new[]
        {
            new AutoModelRouter.RouterCandidate("DeepSeek-V4-Pro", "Azure Foundry", "DeepSeek V4 Pro"),
            new AutoModelRouter.RouterCandidate("z-ai/glm-5.2", "OpenRouter", null),
        };
        var longTask = new string('x', AutoModelRouter.MaxTaskChars + 500);

        var messages = AutoModelRouter.BuildMessages(candidates, longTask);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        var user = messages[1].Text;
        Assert.Contains("DeepSeek-V4-Pro", user);
        Assert.Contains("z-ai/glm-5.2", user);
        Assert.Contains("Azure Foundry", user);
        // The task is capped: the full 500-over payload must not survive into the prompt.
        Assert.DoesNotContain(longTask, user);
        Assert.Contains(new string('x', 100), user);
    }

    [Fact]
    public void UsableCandidates_DropsRouters_Duplicates_AndKeylessModels_InCatalogOrder()
    {
        var loaded = new List<ModelInfo>
        {
            new() { Name = "auto", Provider = "Auto", IsRouter = true, Order = -10 },
            new() { Name = "DeepSeek-V4-Pro", Provider = "Azure Foundry", Order = 2 },
            new() { Name = "DeepSeek-V4-Pro", Provider = "Azure Foundry", Order = 3 }, // duplicate id
            new() { Name = "z-ai/glm-5.2", Provider = "OpenRouter", Order = 6 },
            new() { Name = "keyless-model", Provider = "OpenRouter", Order = 7 },
        };

        var candidates = AutoModelRouter.UsableCandidates(
            loaded, id => id != "keyless-model");

        Assert.Equal(
            new[] { "DeepSeek-V4-Pro", "z-ai/glm-5.2" },
            candidates.Select(c => c.Id).ToArray());
    }
}

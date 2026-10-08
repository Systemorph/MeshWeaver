using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A live node can carry typed content, a deserialized JsonElement, or an as-written JsonObject.
/// All three shapes must give the in-memory query evaluator the same content selectors and text.
/// </summary>
public class QueryEvaluatorJsonObjectContentTest
{
    [Fact]
    public void ContentSelectorsAndTextSearchReadAllThreeContentShapes()
    {
        const string json = """{"hostedIn":"AI/AiThreads","settings":{"region":"EU"},"text":"distinctive-search-needle"}""";
        object[] shapes =
        [
            new ContentShape("AI/AiThreads", new SettingsShape("EU"), "distinctive-search-needle"),
            JsonSerializer.Deserialize<JsonElement>(json),
            JsonNode.Parse(json)!.AsObject(),
        ];
        var evaluator = new QueryEvaluator();
        var parser = new QueryParser();

        foreach (var content in shapes)
        {
            var node = new MeshNode("plugin", "Store") { Content = content };
            var shapeName = content.GetType().Name;

            Assert.True(evaluator.Matches(node, parser.Parse("content.hostedIn:AI/AiThreads")), shapeName);
            Assert.True(evaluator.Matches(node, parser.Parse("hostedIn:AI/AiThreads")), shapeName);
            Assert.True(evaluator.Matches(node, parser.Parse("content.settings.region:EU")), shapeName);
            Assert.True(evaluator.Matches(node, parser.Parse("content.HOSTEDIN:AI/AiThreads")), shapeName);
            Assert.False(evaluator.Matches(node, parser.Parse("content.hostedIn:Elsewhere")), shapeName);
            Assert.True(evaluator.Matches(node, parser.Parse("distinctive-search-needle")), shapeName);
        }
    }

    [Fact]
    public void JsonObjectScalarsKeepTheSameTypesAsJsonElementScalars()
    {
        var domNode = new MeshNode("plugin", "Store")
        {
            Content = new JsonObject { ["rank"] = 42, ["enabled"] = true },
        };
        var elementNode = new MeshNode("plugin", "Store")
        {
            Content = JsonSerializer.Deserialize<JsonElement>("""{"rank":42,"enabled":true}"""),
        };
        var evaluator = new QueryEvaluator();

        Assert.Equal(evaluator.GetPropertyValue(elementNode, "content.rank"),
            evaluator.GetPropertyValue(domNode, "content.rank"));
        Assert.Equal(evaluator.GetPropertyValue(elementNode, "content.enabled"),
            evaluator.GetPropertyValue(domNode, "content.enabled"));
    }

    private sealed record ContentShape(string HostedIn, SettingsShape Settings, string Text);
    private sealed record SettingsShape(string Region);
}

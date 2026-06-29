using System.Text.Json.Nodes;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// Assertions over a parsed <see cref="JsonNode"/> tree — the System.Text.Json replacement for the
/// FluentAssertions.Json element asserts (<c>HaveElement</c> / <c>HaveValue</c>). Begin with
/// <see cref="JsonAssertionExtensions.Should(JsonNode?)"/> or <see cref="JsonAssertionExtensions.BeValidJson"/>.
/// </summary>
public class JsonNodeAssertions(JsonNode? subject) : ObjectAssertions<JsonNode?, JsonNodeAssertions>(subject)
{
    /// <summary>Asserts the current JSON object has a property named <paramref name="name"/>; the match (<c>.Which</c>) is its value.</summary>
    public AndWhichConstraint<JsonNodeAssertions, JsonNode> HaveElement(string name, string because = "", params object[] becauseArgs)
    {
        var child = Subject is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;
        Az.Ensure(child is not null,
            () => $"Expected JSON to have element {Az.Fmt(name)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(Subject?.ToJsonString())}.");
        return new AndWhichConstraint<JsonNodeAssertions, JsonNode>(this, child!);
    }

    /// <summary>Asserts the current JSON value's textual form equals <paramref name="expected"/> (scalar leaf).</summary>
    public AndConstraint<JsonNodeAssertions> HaveValue(string? expected, string because = "", params object[] becauseArgs)
    {
        var actual = Subject?.ToString();
        Az.Ensure(actual == expected,
            () => $"Expected JSON value to be {Az.Fmt(expected)}{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(actual)}.");
        return new(this);
    }
}

/// <summary>JSON-flavoured entry points added to the core assertion surface.</summary>
public static class JsonAssertionExtensions
{
    /// <summary>Begins assertions over a parsed <see cref="JsonNode"/>.</summary>
    public static JsonNodeAssertions Should(this JsonNode? node) => new(node);

    /// <summary>Asserts the string parses as JSON; the match (<c>.Which</c>) is the parsed root <see cref="JsonNode"/>.</summary>
    public static AndWhichConstraint<StringAssertions, JsonNode> BeValidJson(this StringAssertions assertions, string because = "", params object[] becauseArgs)
    {
        JsonNode? node = null;
        try { node = assertions.Subject is { } text ? JsonNode.Parse(text) : null; }
        catch { /* not valid JSON — reported below */ }
        Az.Ensure(node is not null,
            () => $"Expected string to be valid JSON{Az.Reason(because, becauseArgs)}, but found {Az.Fmt(assertions.Subject)}.");
        return new AndWhichConstraint<StringAssertions, JsonNode>(assertions, node!);
    }

    /// <summary>Casts the value under assertion to <typeparamref name="T"/> (FA-compatible <c>.As&lt;T&gt;()</c>).</summary>
    public static T As<T>(this object? subject) => (T)subject!;
}

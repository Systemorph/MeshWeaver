using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the contract of <see cref="ObjectAsExtensions.As{T}"/>: a read NEVER faults its caller.
/// Every case here is one the accessor exists to absorb — the whole point is that an unconvertible
/// value comes back as <c>null</c> with a diagnosis, not as an exception thrown at a hub handler.
/// </summary>
public class ObjectAsExtensionsTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private record Payload(string Name, int Count);

    private record DifferentlyNamed(string Name);

    [Fact]
    public void ContentAs_OnANullNode_ReturnsNull_AndDoesNotThrow()
    {
        // ContentAs is written as `node?.Content.As<T>(options, logger, node.Path)`. The null
        // conditional short-circuits the WHOLE chain — including evaluation of the `node.Path`
        // argument — so a null node is absorbed rather than dereferenced. This test exists because
        // that is easy to misread as a null dereference on review; if someone ever rewrites the
        // expression in a way that evaluates the arguments eagerly, this fails.
        MeshNode? node = null;

        var thrown = Record.Exception(() => node.ContentAs<Payload>(Options));

        thrown.Should().BeNull();
        node.ContentAs<Payload>(Options).Should().BeNull();
    }

    [Fact]
    public void As_OnNull_ReturnsNull() =>
        ((object?)null).As<Payload>(Options).Should().BeNull();

    [Fact]
    public void As_OnAnAlreadyTypedValue_ReturnsItUnchanged()
    {
        var payload = new Payload("acme", 3);

        payload.As<Payload>(Options).Should().BeSameAs(payload);
    }

    [Fact]
    public void As_OnADegradedJsonElement_Deserializes()
    {
        object degraded = JsonSerializer.Deserialize<JsonElement>("""{"name":"acme","count":3}""");

        degraded.As<Payload>(Options).Should().Be(new Payload("acme", 3));
    }

    [Fact]
    public void As_OnAJsonNode_Deserializes()
    {
        object node = JsonNode.Parse("""{"name":"acme","count":3}""")!;

        node.As<Payload>(Options).Should().Be(new Payload("acme", 3));
    }

    [Fact]
    public void As_OnJsonOfTheWrongShape_ReturnsNull_WithoutThrowing()
    {
        // A JsonException on the JsonElement path: the reason the branch catches at all.
        object degraded = JsonSerializer.Deserialize<JsonElement>("""{"name":"acme","count":"not-a-number"}""");

        var thrown = Record.Exception(() => degraded.As<Payload>(Options));

        thrown.Should().BeNull();
        degraded.As<Payload>(Options).Should().BeNull();
    }

    [Fact]
    public void As_WhenTheTargetCannotBeConstructed_ReturnsNull_WithoutThrowing()
    {
        // An abstract target with no registered converter makes System.Text.Json throw
        // NotSupportedException — which does NOT derive from JsonException. Catching only
        // JsonException would let this fault the caller, and "a read never throws" is the entire
        // contract of this accessor. This is the regression the widened catch guards.
        object degraded = JsonSerializer.Deserialize<JsonElement>("""{"name":"acme"}""");

        var thrown = Record.Exception(() => degraded.As<Unconstructible>(Options));

        thrown.Should().BeNull();
        degraded.As<Unconstructible>(Options).Should().BeNull();
    }

    [Fact]
    public void As_OnADifferentlyNamedType_ReturnsNull()
    {
        // Probe-dispatch call sites depend on this: `x.As<Index>() ?? treat x as the item itself`.
        object other = new DifferentlyNamed("acme");

        other.As<Payload>(Options).Should().BeNull();
    }

    /// <summary>A target System.Text.Json cannot construct: abstract, with no registered converter.
    /// Deserializing into it throws <see cref="NotSupportedException"/>, which is deliberately NOT a
    /// <see cref="JsonException"/> — that is the point of the test that uses it.</summary>
    private abstract class Unconstructible
    {
        public string Name { get; init; } = string.Empty;
    }
}

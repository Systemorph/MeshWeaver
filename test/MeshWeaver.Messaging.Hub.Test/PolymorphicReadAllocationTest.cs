using System;
using System.Text;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// MeshWeaver#5555: the <c>[HEAPSTEP]</c> sampler found every multi-hundred-MiB heap step on both
/// portals dominated by <c>System.String</c> ≈ 2 × <c>System.Byte[]</c> plus <c>JsonDocument</c> —
/// a JSON value held once as UTF-8 and once as UTF-16. <c>ObjectPolymorphicConverter.ReadObject</c>
/// did exactly that for every <c>object</c>-typed value: <c>JsonDocument.ParseValue</c> (a byte[]
/// copy + metadata), then <c>GetRawText()</c> (a UTF-16 string, 2× the bytes), then a nested
/// <c>Deserialize(string)</c> — and once PER NESTING LEVEL, because each nested <c>object</c> member
/// re-enters the converter and copies its whole subtree again.
///
/// <para>The reading here is deterministic: <see cref="GC.GetAllocatedBytesForCurrentThread"/> around
/// one deserialization of a value nested <see cref="Depth"/> levels deep whose leaf holds a
/// <see cref="LeafChars"/>-character string. The floor is the leaf string itself (2 bytes per char);
/// the defect costs roughly 3 × that per level.</para>
/// </summary>
public class PolymorphicReadAllocationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int Depth = 8;
    private const int LeafChars = 200_000;

    private record AllocWrapper(object? Inner);

    private record AllocLeaf(string Text);

    private JsonSerializerOptions RegisteredOptions()
    {
        var host = GetHost();
        var registry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.WithType(typeof(AllocWrapper), nameof(AllocWrapper));
        registry.WithType(typeof(AllocLeaf), nameof(AllocLeaf));
        return host.JsonSerializerOptions;
    }

    private static byte[] NestedJson(bool typeFirst)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Depth; i++)
            sb.Append(typeFirst
                ? $"{{\"$type\":\"{nameof(AllocWrapper)}\",\"inner\":"
                : $"{{\"inner\":");
        sb.Append($"{{\"$type\":\"{nameof(AllocLeaf)}\",\"text\":\"").Append('a', LeafChars).Append("\"}");
        for (var i = 0; i < Depth; i++)
            sb.Append(typeFirst ? "}" : $",\"$type\":\"{nameof(AllocWrapper)}\"}}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AssertChain(object? value)
    {
        for (var i = 0; i < Depth; i++)
        {
            value.Should().BeOfType<AllocWrapper>($"level {i} carries a registered $type");
            value = ((AllocWrapper)value!).Inner;
        }
        value.Should().BeOfType<AllocLeaf>().Which.Text.Length.Should().Be(LeafChars);
    }

    [Fact]
    public void NestedTypedRead_AllocatesTheLeafOnce_NotACopyPerLevel()
    {
        var options = RegisteredOptions();
        var utf8 = NestedJson(typeFirst: true);

        // Warm-up: type-info resolution and pooled buffers are one-time costs, not per-read ones.
        AssertChain(JsonSerializer.Deserialize<object>(utf8, options));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = JsonSerializer.Deserialize<object>(utf8, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertChain(result);
        var leafBytes = 2L * LeafChars;
        Output.WriteLine($"allocated {allocated:N0} bytes for a {leafBytes:N0}-byte leaf at depth {Depth}");
        allocated.Should().BeLessThan(2 * leafBytes,
            "a typed read materialises the leaf string once; a JsonDocument copy plus a UTF-16 "
            + "string per nesting level is the String ≈ 2×Byte[] + JsonDocument churn of #5555");
    }

    /// <summary>
    /// The general path (<c>$type</c> not first — needs reordering) must stay correct and must not
    /// build a UTF-16 string either; it still pays one JsonDocument per level, which is why the
    /// bound is looser than the direct path's.
    /// </summary>
    [Fact]
    public void NestedTypedRead_TypeNotFirst_StillTypedAndStringFree()
    {
        var options = RegisteredOptions();
        var utf8 = NestedJson(typeFirst: false);

        AssertChain(JsonSerializer.Deserialize<object>(utf8, options));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = JsonSerializer.Deserialize<object>(utf8, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertChain(result);
        var leafBytes = 2L * LeafChars;
        Output.WriteLine($"allocated {allocated:N0} bytes for a {leafBytes:N0}-byte leaf at depth {Depth} ($type last)");
        allocated.Should().BeLessThan((Depth + 2) * leafBytes,
            "without the UTF-16 round trip each level costs about one UTF-8 copy (half the leaf's "
            + "UTF-16 size) plus a reorder buffer, not the ~3× the string round trip cost");
    }

    /// <summary>A registered type whose JSON no longer fits is preserved as raw JSON, not thrown.</summary>
    [Fact]
    public void RegisteredTypeThatDoesNotFit_IsPreservedAsRawJson()
    {
        var options = RegisteredOptions();
        var json = $"{{\"$type\":\"{nameof(AllocLeaf)}\",\"text\":{{\"not\":\"a string\"}}}}";

        var result = JsonSerializer.Deserialize<object>(json, options);

        result.Should().BeOfType<JsonElement>().Which.GetProperty("text").GetProperty("not").GetString()
            .Should().Be("a string");
    }
}

using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Layout;

/// <summary>
/// How an analysis view's row property resolves to the rows themselves.
///
/// <para><see cref="KpiStripControl.Items"/>, <see cref="TowerControl.Bands"/> and
/// <see cref="ComparisonBarsControl.Pairs"/> are declared <c>object</c> so a caller may hand over the
/// rows directly OR bind them to a live data section — and those two arrive at a renderer
/// differently: a direct hand-over keeps its CLR type, a pointer-resolved one comes off the
/// synchronization stream as a <see cref="JsonElement"/>. Both are the same rows, so the resolution
/// belongs to the framework rather than to each renderer.</para>
/// </summary>
public static class AnalysisRows
{
    /// <summary>
    /// Resolves a bound row property to a typed list. Anything that is not readable as rows — null,
    /// a pointer that resolved to nothing, a value of the wrong shape — is an EMPTY list, so the
    /// view falls to its "nothing to show" state rather than drawing a half-populated frame.
    /// </summary>
    /// <typeparam name="T">The row record type.</typeparam>
    /// <param name="value">The raw value of the control's row property.</param>
    /// <param name="options">Serializer options used for the JSON path.</param>
    /// <returns>The rows; never null.</returns>
    public static ImmutableList<T> Resolve<T>(object? value, JsonSerializerOptions? options = null) =>
        value switch
        {
            null => [],
            ImmutableList<T> already => already,
            IEnumerable<T> typed => typed.ToImmutableList(),
            JsonElement { ValueKind: JsonValueKind.Array } json =>
                json.Deserialize<List<T>>(options)?.ToImmutableList() ?? [],
            _ => [],
        };
}

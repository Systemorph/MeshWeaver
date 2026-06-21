using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Reproduces + pins the fix for the DataGrid render crash (atioz 2026-06-21): a column whose
/// enum-typed property carried a mis-cased literal — <c>HorizontalAlignment = "center"</c> from a
/// node's Source — crashed the Blazor render. <see cref="LayoutClientExtensions.GetDataBoundValue{T}"/>
/// did a case-SENSITIVE <c>Enum.Parse</c> that threw <see cref="System.ArgumentException"/>
/// ("Requested value 'center' was not found") inside <c>DataGridView.RenderPropertyColumn</c> →
/// the exception escaped <c>BuildRenderTree</c>, tore down the Blazor circuit, and hung the page
/// with no feedback. The fix is a case-INSENSITIVE, non-throwing <c>TryParse</c>: resolve the
/// common mis-cased value, fall back to default for a genuinely unknown literal, never throw.
/// </summary>
public class GetDataBoundValueEnumTest
{
    private enum Align { Start, Center, End }

    // The enum branch of GetDataBoundValue returns before it ever touches the stream, so a null
    // stream reference is safe on this path (extension method; no dereference). Keeps this a pure
    // unit test of the enum-parse behaviour with no hub/stream harness.
    private static readonly ISynchronizationStream<JsonElement> NoStream = null!;

    [Fact]
    public void MisCased_enum_literal_resolves_case_insensitively()
    {
        // Without the fix this THREW ArgumentException (the atioz render crash); with it, "center"
        // resolves to Align.Center.
        Assert.Equal(Align.Center, NoStream.GetDataBoundValue<Align>("center", null));
    }

    [Fact]
    public void Unknown_enum_literal_returns_default_instead_of_throwing()
    {
        // An unknown literal must fall back to default — never throw mid-render and kill the circuit.
        Assert.Equal(default, NoStream.GetDataBoundValue<Align>("not-an-alignment", null));
    }

    [Fact]
    public void Exact_case_enum_literal_still_resolves()
    {
        Assert.Equal(Align.End, NoStream.GetDataBoundValue<Align>("End", null));
    }
}

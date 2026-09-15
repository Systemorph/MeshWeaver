// <meshweaver>
// Id: Testing/Layout/GetDataBoundValueEnumTest
// DisplayName: Testing/Layout/GetDataBoundValueEnumTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout.Client;

/// <summary>
/// Reproduces + pins the fix for the DataGrid render crash (prod 2026-06-21): a column whose
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

    [MeshFact]
    public void MisCased_enum_literal_resolves_case_insensitively()
    {
        // Without the fix this THREW ArgumentException (the prod render crash); with it, "center"
        // resolves to Align.Center.
        Assert.Equal(Align.Center, NoStream.GetDataBoundValue<Align>("center", null));
    }

    [MeshFact]
    public void Unknown_enum_literal_returns_default_instead_of_throwing()
    {
        // An unknown literal must fall back to default — never throw mid-render and kill the circuit.
        Assert.Equal(default, NoStream.GetDataBoundValue<Align>("not-an-alignment", null));
    }

    [MeshFact]
    public void Exact_case_enum_literal_still_resolves()
    {
        Assert.Equal(Align.End, NoStream.GetDataBoundValue<Align>("End", null));
    }

    // ---- Non-enum value conversions must ALSO never throw mid-render -------------------------------
    // GetDataBoundValue runs inside DataGridView.RenderPropertyColumn (BuildRenderTree). Before the
    // hardening, a value that Convert.ChangeType / a direct unbox cast couldn't handle threw
    // (FormatException / InvalidCastException) and tore down the circuit on a parameter switch
    // (year/PK). It must now fall back to default and keep rendering.

    [MeshFact]
    public void Unconvertible_string_to_value_type_returns_default_instead_of_throwing()
    {
        // "not-a-bool" cannot be parsed as bool — previously a FormatException killed the render.
        Assert.False(NoStream.GetDataBoundValue<bool>("not-a-bool", null));
    }

    [MeshFact]
    public void Mismatched_boxed_value_returns_default_instead_of_throwing()
    {
        // A boxed object that is neither T nor IConvertible-compatible must not throw an
        // InvalidCastException inside BuildRenderTree.
        Assert.Equal(0, NoStream.GetDataBoundValue<int>(new object(), null));
    }

    [MeshFact]
    public void Convertible_string_still_converts()
    {
        // The defensive try/catch must not regress the happy path.
        Assert.Equal(42, NoStream.GetDataBoundValue<int>("42", null));
    }
}

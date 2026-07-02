// <meshweaver>
// Id: PandasExplorer
// DisplayName: Pandas Explorer Data Model
// </meshweaver>

using System;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

/// <summary>
/// Content of a <c>PandasExplorer</c> node — the marker record for the interactive frontend
/// (<see cref="PandasExplorerLayoutAreas"/>) that DRIVES the Python <c>py/pandas</c> participant
/// (<c>clients/python/meshweaver/examples/pandas_node.py</c>). The node holds no data itself; the
/// live <c>pandas.DataFrame</c> lives in the Python process. This record only carries the display
/// name so the node is addressable and browsable.
/// </summary>
public record PandasExplorer
{
    /// <summary>Display name of the explorer node (mirrored onto <see cref="MeshNode.Name"/>).</summary>
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional blurb rendered above the interactive controls.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// The wire message the frontend POSTS to the <c>py/pandas</c> participant. It is the .NET half of the
/// Python <c>PandasCommand</c> protocol (<c>load</c> / <c>append</c> / <c>reset</c> mutate the held
/// frame; <c>render</c> / <c>groupby</c> / <c>rolling</c> / <c>describe</c> return a grid). The mesh
/// serialises this with the <c>$type: "PandasCommand"</c> discriminator (the short type name) and
/// camelCase field names — exactly what <c>pandas_node.PandasNode._apply</c> reads off the message.
/// The participant answers grid commands with a <see cref="DataGridControl"/> and mutations with an ack.
/// </summary>
public record PandasCommand : IRequest<DataGridControl>
{
    /// <summary>Command verb: <c>load</c>, <c>append</c>, <c>reset</c>, <c>render</c>, <c>groupby</c>, <c>rolling</c> or <c>describe</c>.</summary>
    public string Command { get; init; } = "render";

    /// <summary>Group-by column for <c>groupby</c>.</summary>
    public string? By { get; init; }

    /// <summary>Aggregation for <c>groupby</c> (e.g. <c>sum</c>, <c>mean</c>).</summary>
    public string? Agg { get; init; }

    /// <summary>Target column for <c>rolling</c>.</summary>
    public string? Column { get; init; }

    /// <summary>Window size for <c>rolling</c>.</summary>
    public int? Window { get; init; }

    /// <summary>Rows to concatenate for <c>append</c>.</summary>
    public object[]? Rows { get; init; }

    /// <summary>Records to replace the frame with for <c>load</c>.</summary>
    public object[]? Data { get; init; }

    /// <summary>A monthly sales frame across two regions — the same showcase dataset the Python demo seeds.</summary>
    public static object[] SampleSales() =>
    [
        new { month = "Jan", region = "EMEA", sales = 120.0, units = 12 },
        new { month = "Feb", region = "EMEA", sales = 135.5, units = 14 },
        new { month = "Mar", region = "EMEA", sales = 128.0, units = 13 },
        new { month = "Apr", region = "APAC", sales = 98.0, units = 9 },
        new { month = "May", region = "APAC", sales = 143.0, units = 15 },
        new { month = "Jun", region = "APAC", sales = 150.0, units = 16 },
    ];

    /// <summary>Replace the held frame with the sample sales data.</summary>
    public static PandasCommand LoadSample() => new() { Command = "load", Data = SampleSales() };

    /// <summary>Append two more months over the mesh — mutates the held frame.</summary>
    public static PandasCommand AppendTwo() => new()
    {
        Command = "append",
        Rows =
        [
            new { month = "Jul", region = "APAC", sales = 161.0, units = 17 },
            new { month = "Aug", region = "EMEA", sales = 152.0, units = 15 },
        ],
    };

    /// <summary>Clear the held frame.</summary>
    public static PandasCommand Reset() => new() { Command = "reset" };
}

/// <summary>
/// The frontend's internal trigger record: which read-only view of the frame to render next. Buttons
/// flip this in the layout-area data store; the grid sub-area re-observes it and posts the matching
/// <see cref="PandasCommand"/>. The <see cref="Nonce"/> makes every button press a distinct value so a
/// repeated command (e.g. two "Refresh" clicks) still re-fires through <c>DistinctUntilChanged</c>.
/// Only grid-producing verbs live here — mutations post directly and then flip this to <c>render</c>.
/// </summary>
public record PandasViewCommand
{
    /// <summary>Grid-producing verb: <c>render</c>, <c>groupby</c>, <c>rolling</c> or <c>describe</c>.</summary>
    public string Command { get; init; } = "render";

    /// <summary>Group-by column when <see cref="Command"/> is <c>groupby</c>.</summary>
    public string? By { get; init; }

    /// <summary>Rolling column when <see cref="Command"/> is <c>rolling</c>.</summary>
    public string? Column { get; init; }

    /// <summary>Rolling window when <see cref="Command"/> is <c>rolling</c>.</summary>
    public int? Window { get; init; }

    /// <summary>Distinct token so identical commands still re-emit through DistinctUntilChanged.</summary>
    public string Nonce { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Render the frame's current state.</summary>
    public static PandasViewCommand Render() => new();

    /// <summary>Group the frame by <paramref name="by"/> (sum).</summary>
    public static PandasViewCommand GroupBy(string by) => new() { Command = "groupby", By = by };

    /// <summary>Add a rolling mean of <paramref name="column"/> over <paramref name="window"/> rows.</summary>
    public static PandasViewCommand Rolling(string column, int window) =>
        new() { Command = "rolling", Column = column, Window = window };

    /// <summary>Descriptive statistics of the frame.</summary>
    public static PandasViewCommand Describe() => new() { Command = "describe" };

    /// <summary>Projects this view onto the wire <see cref="PandasCommand"/> the participant understands.</summary>
    public PandasCommand ToCommand() => new()
    {
        Command = Command,
        By = By,
        Agg = By is null ? null : "sum",
        Column = Column,
        Window = Window,
    };

    /// <summary>Human-readable caption for the rendered view.</summary>
    public string Caption() => Command switch
    {
        "groupby" => $"grouped by {By} (sum)",
        "rolling" => $"{Window}-row rolling mean of {Column}",
        "describe" => "descriptive statistics",
        _ => "current state",
    };
}

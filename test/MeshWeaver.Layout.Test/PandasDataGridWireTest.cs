using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Cross-language proof for <c>meshweaver.examples.pandas_node</c> (the Python-backed mesh node that holds
/// a live pandas DataFrame): the DataGrid JSON that node emits is the SAME wire contract the C# GUI
/// renders. The <see cref="PythonPandasNodeWire"/> constant below is captured VERBATIM from
/// <c>python -m meshweaver.examples.pandas_node --demo</c> (with the <c>$type</c> the participant's
/// <c>respond("DataGridControl", …)</c> stamps on the wire). We deserialize those exact bytes with the
/// mesh's own <see cref="JsonSerializerOptions"/> and prove they become a real, typed
/// <see cref="DataGridControl"/> — not markdown, not an HTML string.
/// </summary>
public class PandasDataGridWireTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(x => x);

    // Captured verbatim from `python -m meshweaver.examples.pandas_node --demo`, wrapped with the
    // top-level "$type":"DataGridControl" that MeshConnection.respond stamps on the outbound message.
    private const string PythonPandasNodeWire =
        """
        {
          "$type": "DataGridControl",
          "data": [
            {"month":"Jan","region":"EMEA","sales":120.0,"units":12},
            {"month":"Feb","region":"EMEA","sales":135.5,"units":14},
            {"month":"Mar","region":"EMEA","sales":128.0,"units":13},
            {"month":"Apr","region":"APAC","sales":98.0,"units":9},
            {"month":"May","region":"APAC","sales":143.0,"units":15},
            {"month":"Jun","region":"APAC","sales":150.0,"units":16},
            {"month":"Jul","region":"APAC","sales":161.0,"units":17},
            {"month":"Aug","region":"EMEA","sales":152.0,"units":15}
          ],
          "columns": [
            {"$type":"PropertyColumnControl`1[String]","property":"month","title":"Month"},
            {"$type":"PropertyColumnControl`1[String]","property":"region","title":"Region"},
            {"$type":"PropertyColumnControl`1[Double]","property":"sales","title":"Sales","format":"N2"},
            {"$type":"PropertyColumnControl`1[Int64]","property":"units","title":"Units","format":"N0"}
          ]
        }
        """;

    [HubFact]
    public void PythonPandasNode_DataGrid_DeserializesToARealDataGridControl()
    {
        var host = GetHost();

        // The exact Python bytes deserialize, via the mesh's own options, into a first-class UiControl.
        var control = JsonSerializer.Deserialize<UiControl>(PythonPandasNodeWire, host.JsonSerializerOptions);
        var grid = Assert.IsType<DataGridControl>(control);

        // Four columns, one per DataFrame dtype. The Python `$type` discriminators (a literal backtick;
        // C# emits the same character as ` — wire-equivalent JSON) each resolve to the EXACT typed
        // PropertyColumnControl<T> the grid renderer binds. That is what makes it a real, sortable,
        // correctly-formatted grid rather than opaque JSON — the columns deserialize to typed controls.
        Assert.Equal(4, grid.Columns.Count);

        var month = Assert.IsType<PropertyColumnControl<string>>(grid.Columns[0]);
        Assert.Equal("month", month.Property);
        Assert.IsType<PropertyColumnControl<string>>(grid.Columns[1]);

        // The numeric columns are typed AND carry the .NET format strings the grid uses to format cells.
        var sales = Assert.IsType<PropertyColumnControl<double>>(grid.Columns[2]);
        Assert.Equal("sales", sales.Property);
        Assert.Equal("N2", sales.Format?.ToString());
        var units = Assert.IsType<PropertyColumnControl<long>>(grid.Columns[3]);
        Assert.Equal("N0", units.Format?.ToString());

        // The rows are the live frame's data — including the two rows appended over the mesh in the demo.
        var data = (JsonElement)grid.Data;
        Assert.Equal(8, data.GetArrayLength());
        var jul = data[6];
        Assert.Equal("Jul", jul.GetProperty("month").GetString());
        Assert.Equal(161.0, jul.GetProperty("sales").GetDouble());
        Assert.Equal(17, jul.GetProperty("units").GetInt64());
    }

    [HubFact]
    public void CSharp_EquivalentDataGrid_ProducesTheSameColumnDiscriminators()
    {
        var host = GetHost();

        // The hand-written C# equivalent of what the Python node emits — the exact idiom from the Pricing
        // sample. Serialising it shows the C# side produces the SAME column $type discriminators, so the
        // Python node is speaking the framework's own DataGrid contract.
        var grid = Controls.DataGrid(new object[]
            {
                new { month = "Jan", region = "EMEA", sales = 120.0, units = 12L },
            })
            .WithColumn(new PropertyColumnControl<string> { Property = "month" }.WithTitle("Month"))
            .WithColumn(new PropertyColumnControl<string> { Property = "region" }.WithTitle("Region"))
            .WithColumn(new PropertyColumnControl<double> { Property = "sales" }.WithTitle("Sales").WithFormat("N2"))
            .WithColumn(new PropertyColumnControl<long> { Property = "units" }.WithTitle("Units").WithFormat("N0"));

        var json = JsonSerializer.Serialize(grid, host.JsonSerializerOptions);
        Output.WriteLine(json);

        // The C# JSON encoder escapes the generic-discriminator backtick as ` (the Python node emits
        // the literal backtick — both decode to the same string), so assert on the encoder-independent
        // outcome: the C# grid round-trips back into the SAME typed columns the Python wire deserialises to.
        var roundtrip = Assert.IsType<DataGridControl>(
            JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions));
        Assert.Equal(4, roundtrip.Columns.Count);
        Assert.IsType<PropertyColumnControl<string>>(roundtrip.Columns[0]);
        Assert.IsType<PropertyColumnControl<double>>(roundtrip.Columns[2]);
        Assert.IsType<PropertyColumnControl<long>>(roundtrip.Columns[3]);
    }
}

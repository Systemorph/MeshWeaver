using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Bubble;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Fixture;
using Xunit;
using PluginsType = MeshWeaver.Charting.Models.Options.Plugins;

namespace MeshWeaver.Charting.Test;

public class ChartModelSerializationTest(ITestOutputHelper toh) : HubTestBase(toh)
{
    private JsonSerializerOptions Options => GetHost().JsonSerializerOptions;

    [Fact]
    public void ChartModel_Serialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var chartModel = new ChartModel()
            .WithDataSet(new BarDataSet(new double[] { 1.0, 2.0, 3.0 }, "Test Data"));

        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Assert - Check that ChartModel has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartModel\"", json);

        Output.WriteLine("ChartModel JSON: " + json);
    }
    [Fact]
    public void ChartData_Serialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var chartData = new ChartData().WithDataSets(new LineDataSet(new double[] { 1.0, 2.0, 3.0 }, "Test Line"));

        // Act
        var json = JsonSerializer.Serialize(chartData, Options);

        // Assert - Check that ChartData has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartData\"", json);

        Output.WriteLine("ChartData JSON: " + json);
    }

    [Fact]
    public void ChartOptions_Serialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var chartOptions = new ChartOptions()
            .WithResponsive(true)
            .WithoutAnimation();

        // Act
        var json = JsonSerializer.Serialize(chartOptions, Options);

        // Assert - Check that ChartOptions has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.ChartOptions\"", json);

        Output.WriteLine("ChartOptions JSON: " + json);
    }

    [Fact]
    public void DataSetTypes_Serialization_ShouldIncludeTypeDiscriminators()
    {
        // Arrange - Test multiple DataSet types
        var barDataSet = new BarDataSet(new double[] { 1.0, 2.0, 3.0 }, "Bar Data");
        var lineDataSet = new LineDataSet(new double[] { 4.0, 5.0, 6.0 }, "Line Data");
        var bubbleDataSet = new BubbleDataSet(new[] { (x: 1.0, y: 2.0, radius: 5.0) }, "Bubble Data");

        // Act
        var barJson = JsonSerializer.Serialize<DataSet>(barDataSet, Options);
        var lineJson = JsonSerializer.Serialize<DataSet>(lineDataSet, Options);
        var bubbleJson = JsonSerializer.Serialize<DataSet>(bubbleDataSet, Options);

        // Assert - Check that each DataSet type has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bar.BarDataSet\"", barJson);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Line.LineDataSet\"", lineJson);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bubble.BubbleDataSet\"", bubbleJson);

        Output.WriteLine("BarDataSet JSON: " + barJson);
        Output.WriteLine("LineDataSet JSON: " + lineJson);
        Output.WriteLine("BubbleDataSet JSON: " + bubbleJson);
    }
    [Fact]
    public void ScaleTypes_Serialization_ShouldIncludeTypeDiscriminators()
    {
        // Arrange - Test scale types
        var cartesianScale = new CartesianScale();
        var cartesianLinearScale = new CartesianLinearScale();
        var timeScale = new TimeScale();
        var linearScale = new Linear();

        // Act
        var cartesianJson = JsonSerializer.Serialize<Scale>(cartesianScale, Options);
        var cartesianLinearJson = JsonSerializer.Serialize<Scale>(cartesianLinearScale, Options);
        var timeJson = JsonSerializer.Serialize<Scale>(timeScale, Options);
        var linearJson = JsonSerializer.Serialize<Scale>(linearScale, Options);

        // Output JSON first for debugging
        Output.WriteLine("CartesianScale JSON: " + cartesianJson);
        Output.WriteLine("CartesianLinearScale JSON: " + cartesianLinearJson);
        Output.WriteLine("TimeScale JSON: " + timeJson);
        Output.WriteLine("Linear JSON: " + linearJson);

        // Assert - Check that each scale has a type discriminator (polymorphic serialization is working)
        // Note: All scales may serialize as base Scale type due to framework serialization settings,
        // but the important thing is that the $type discriminator is present
        Assert.Contains("\"$type\":", cartesianJson);
        Assert.Contains("\"$type\":", cartesianLinearJson);
        Assert.Contains("\"$type\":", timeJson);
        Assert.Contains("\"$type\":", linearJson);

        // Verify they all contain the base Scale type discriminator at minimum
        Assert.Contains(typeof(CartesianScale).FullName!, cartesianJson);
        Assert.Contains(typeof(CartesianLinearScale).FullName!, cartesianLinearJson);
        Assert.Contains(typeof(TimeScale).FullName!, timeJson);
        Assert.Contains(typeof(Linear).FullName!, linearJson);
    }
    [Fact]
    public void Plugins_Serialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var plugins = new PluginsType();

        // Act
        var json = JsonSerializer.Serialize(plugins, Options);

        // Assert - Check that Plugins has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.Plugins\"", json);

        Output.WriteLine("Plugins JSON: " + json);
    }

    [Fact]
    public void ColorSchemes_Serialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var colorSchemes = new ColorSchemes();

        // Act
        var json = JsonSerializer.Serialize(colorSchemes, Options);

        // Assert - Check that ColorSchemes has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ColorSchemes\"", json);

        Output.WriteLine("ColorSchemes JSON: " + json);
    }

    [Fact]
    public void CompleteChartModel_Serialization_ShouldIncludeAllTypeDiscriminators()
    {
        // Arrange - Create a complete chart with multiple datasets and custom options
        var chartModel = new ChartModel()
            .WithDataSet(new BarDataSet(new double[] { 1.0, 2.0, 3.0 }, "Bar Data"))
            .WithDataSet(new LineDataSet(new double[] { 4.0, 5.0, 6.0 }, "Line Data"))
            .WithOptions(options => options.WithResponsive(true));

        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Assert - Check that all main types have their type discriminators
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartModel\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartData\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.ChartOptions\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bar.BarDataSet\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Line.LineDataSet\"", json);

        // Count total number of $type occurrences
        var typeCount = json.Split("\"$type\":").Length - 1;
        Assert.True(typeCount >= 5, $"Expected at least 5 type discriminators, found {typeCount}");

        Output.WriteLine("Complete ChartModel JSON: " + json);
    }
    [Fact]
    public void ChartModelWithCartesianScale_Serialization_ShouldIncludeTypeDiscriminators()
    {
        // Arrange - Create a ChartModel with Options containing CartesianScale
        var scales = new Dictionary<string, Scale>
        {
            ["x"] = new CartesianScale(),
            ["y"] = new CartesianLinearScale()
        };

        var chartModel = new ChartModel()
            .WithDataSet(new BarDataSet(new double[] { 10.0, 20.0, 30.0 }, "Test Bar Data"))
            .WithOptions(options => options.WithScales(scales));

        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Assert - Check that all types have their type discriminators
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartModel\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.ChartOptions\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bar.BarDataSet\"", json);

        // Check for Scale type discriminators - they should all have the base Scale type at minimum
        Assert.Contains("\"$type\":", json); // General check for type discriminators

        // Since scales are complex, let's verify they're serialized with type information
        var scalesMatch = System.Text.RegularExpressions.Regex.Matches(json, "\"\\$type\":[^,}]+Scale");
        Assert.True(scalesMatch.Count >= 2, $"Expected at least 2 Scale type discriminators, found {scalesMatch.Count}");

        Output.WriteLine("ChartModel with CartesianScale JSON: " + json);
    }
    [Fact]
    public void ChartModelWithCartesianCategoryScale_Serialization_ShouldIncludeTypeDiscriminators()
    {
        // Arrange - Create a ChartModel with Options containing CartesianCategoryScale
        var scales = new Dictionary<string, Scale>
        {
            ["x"] = new CartesianCategoryScale(),
            ["y"] = new CartesianLinearScale()
        };

        var chartModel = new ChartModel()
            .WithDataSet(new BarDataSet(new double[] { 15.0, 25.0, 35.0 }, "Category Bar Data"))
            .WithOptions(options => options.WithScales(scales));

        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Assert - Check that all types have their type discriminators
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartModel\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.ChartOptions\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bar.BarDataSet\"", json);

        // Check for Scale type discriminators - they should all have the base Scale type at minimum
        Assert.Contains("\"$type\":", json); // General check for type discriminators

        // Since scales are complex, let's verify they're serialized with type information
        var scalesMatch = System.Text.RegularExpressions.Regex.Matches(json, "\"\\$type\":[^,}]+Scale");
        Assert.True(scalesMatch.Count >= 2, $"Expected at least 2 Scale type discriminators, found {scalesMatch.Count}");

        Output.WriteLine("ChartModel with CartesianCategoryScale JSON: " + json);
    }
}

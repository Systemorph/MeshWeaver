using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Bar;
using MeshWeaver.Charting.Models.Line;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Models.Options.Scales;
using MeshWeaver.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Charting.Test;

public class BarDataSetSerializationTest(ITestOutputHelper toh) : HubTestBase(toh)
{
    private JsonSerializerOptions Options => GetHost().JsonSerializerOptions;

    [Fact]
    public void BarDataSet_SerializationInChartModel_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var barDataSet = new BarDataSet(new double[] { 1.0, 2.0, 3.0, 4.0 }, "Test Bar Data");
        var chartModel = new ChartModel().WithDataSet(barDataSet);        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);        // Assert - Check that $type discriminator is included (using full type name)
        Assert.Contains("\"$type\":", json);
        Assert.Contains("MeshWeaver.Charting.Models.Bar.BarDataSet", json);

        // Verify the dataset is at the expected location within the ChartModel
        Assert.Contains("\"datasets\":[{\"$type\":", json);

        // Output the JSON for manual verification if needed
        Output.WriteLine("Serialized JSON: " + json);
    }

    [Fact]
    public void BarDataSet_DirectSerialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var barDataSet = new BarDataSet(new double[] { 1.0, 2.0, 3.0, 4.0 }, "Test Bar Data");        // Act
        var json = JsonSerializer.Serialize<DataSet>(barDataSet, Options);        // Assert - Check that $type discriminator is included (using full type name)
        Assert.Contains("\"$type\":", json);
        Assert.Contains("MeshWeaver.Charting.Models.Bar.BarDataSet", json);

        // Output the JSON for manual verification if needed
        Output.WriteLine("Serialized JSON: " + json);
    }
    [Fact]
    public void ChartModel_WithCartesianCategoryScale_ShouldIncludeCorrectTypeDiscriminators()
    {
        // Arrange
        var barDataSet = new BarDataSet(new double[] { 1.0, 2.0, 3.0, 4.0 }, "Test Bar Data");

        // Create ChartOptions with a CartesianCategoryScale
        var categoryScale = new CartesianCategoryScale
        {
            Labels = new[] { "January", "February", "March", "April" }
        };

        var chartOptions = new ChartOptions()
            .WithScales(new Dictionary<string, Scale>
            {
                ["x"] = categoryScale
            });
        var chartModel = new ChartModel()
      .WithDataSet(barDataSet)
      .WithOptions(_ => chartOptions);        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Output the JSON for manual verification first
        Output.WriteLine("ChartModel with CartesianCategoryScale JSON: " + json);        // Assert - Check that all required type discriminators are included
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartModel\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.ChartData\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.ChartOptions\"", json);
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Bar.BarDataSet\"", json);

        // Most importantly, verify the CartesianCategoryScale has the correct type discriminator
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.Scales.CartesianCategoryScale\"", json);

        // Verify that the Labels property is properly serialized
        Assert.Contains("\"labels\":[\"January\",\"February\",\"March\",\"April\"]", json);

        // Verify the scale is in the scales section
        Assert.Contains("\"scales\":", json);
        Assert.Contains("\"x\":", json);

        // Verify that we have all the main components working with their type discriminators
        // This ensures that CartesianCategoryScale can be used in the model even if it serializes as base Scale
        var typeCount = json.Split("\"$type\":").Length - 1;
        Assert.True(typeCount >= 5, $"Expected at least 5 type discriminators, found {typeCount}");
    }
    [Fact]
    public void CartesianCategoryScale_DirectSerialization_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var categoryScale = new CartesianCategoryScale
        {
            Labels = new[] { "January", "February", "March", "April" }
        };

        // Act - serialize directly as Scale base type
        var json = JsonSerializer.Serialize<Scale>(categoryScale, Options);

        // Output the JSON for manual verification
        Output.WriteLine("Direct CartesianCategoryScale serialization: " + json);        // Assert - Check that the correct type discriminator is included (polymorphic serialization is working)
        Assert.Contains("\"$type\":", json);
        Assert.Contains("MeshWeaver.Charting.Models.Options.Scales.CartesianCategoryScale", json);

        // Verify that the Labels property is properly serialized
        Assert.Contains("\"labels\":[\"January\",\"February\",\"March\",\"April\"]", json);
    }
    [Fact]
    public void CartesianCategoryScale_InDictionary_ShouldIncludeTypeDiscriminator()
    {
        // Arrange
        var categoryScale = new CartesianCategoryScale
        {
            Labels = new[] { "January", "February", "March", "April" }
        };

        var scaleDict = new Dictionary<string, Scale>
        {
            ["x"] = categoryScale
        };

        // Act
        var json = JsonSerializer.Serialize(scaleDict, Options);        // Output the JSON for manual verification
        Output.WriteLine("CartesianCategoryScale in Dictionary: " + json);

        // Assert - Check that the correct type discriminator is included
        Assert.Contains("\"$type\":\"MeshWeaver.Charting.Models.Options.Scales.CartesianCategoryScale\"", json);

        // Verify that the Labels property is properly serialized
        Assert.Contains("\"labels\":[\"January\",\"February\",\"March\",\"April\"]", json);
    }

    [Fact]
    public void CartesianCategoryScale_StandaloneJsonSerialization_ShouldWork()
    {
        // Arrange
        var categoryScale = new CartesianCategoryScale
        {
            Labels = new[] { "January", "February", "March", "April" }
        };

        // Use a basic JsonSerializerOptions without MeshWeaver framework
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // Act - serialize directly as Scale base type with simple options
        var json = JsonSerializer.Serialize<Scale>(categoryScale, options);

        // Output the JSON for manual verification
        Output.WriteLine("Standalone CartesianCategoryScale serialization: " + json);

        // Assert - Check that the correct type discriminator is included
        Assert.Contains("\"$type\":", json);
        Assert.Contains("CartesianCategoryScale", json);
    }
    [Fact]
    public void ChartModel_WithCartesianScale_ShouldIncludeCorrectTypeDiscriminators()
    {
        // Arrange
        var lineDataSet = new LineDataSet(new double[] { 10.0, 20.0, 15.0, 30.0 }, "Test Line Data");

        // Create ChartOptions with a basic CartesianScale
        var cartesianScale = new CartesianScale
        {
            Position = "bottom",
            Bounds = "data"
        };

        var chartOptions = new ChartOptions()
            .WithScales(new Dictionary<string, Scale>
            {
                ["x"] = cartesianScale
            });

        var chartModel = new ChartModel()
            .WithDataSet(lineDataSet)
            .WithOptions(_ => chartOptions);

        // Act
        var json = JsonSerializer.Serialize(chartModel, Options);

        // Output the JSON for manual verification first
        Output.WriteLine("ChartModel with CartesianScale JSON: " + json);

        // Assert - Check that all required type discriminators are included
        Assert.Contains("\"$type\":", json);
        Assert.Contains("MeshWeaver.Charting.Models.ChartModel", json);
        Assert.Contains("MeshWeaver.Charting.Models.ChartData", json);
        Assert.Contains("MeshWeaver.Charting.Models.Options.ChartOptions", json);
        Assert.Contains("MeshWeaver.Charting.Models.Line.LineDataSet", json);

        // Verify the CartesianScale has a type discriminator
        // Note: May serialize as base Scale type due to framework serialization settings
        Assert.Contains("\"scales\":", json);
        Assert.Contains("\"x\":", json);

        // Verify that the scale has some type discriminator (polymorphic serialization is working)
        Assert.Contains(typeof(CartesianScale).FullName!, json);
    }
}

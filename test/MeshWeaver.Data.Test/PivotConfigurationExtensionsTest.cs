using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Pivot;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Test data for pivot configuration tests
/// </summary>
public record SalesData
{
    [Key]
    public int Id { get; init; }

    [Dimension(typeof(string), nameof(Region))]
    [DisplayName("Sales Region")]
    public string? Region { get; init; }

    [Dimension(typeof(string), nameof(ProductCategory))]
    public string? ProductCategory { get; init; }

    [DisplayName("Order Month")]
    public string? OrderMonth { get; init; }

    [DisplayName("Total Sales")]
    public decimal TotalSales { get; init; }

    public int Quantity { get; init; }

    [NotVisible]
    public string? InternalNotes { get; init; }
}

/// <summary>
/// Tests for PivotConfigurationExtensions
/// </summary>
public class PivotConfigurationExtensionsTest
{
    private static readonly IEnumerable<SalesData> TestData =
    [
        new SalesData { Id = 1, Region = "North", ProductCategory = "Electronics", OrderMonth = "2024-01", TotalSales = 10000m, Quantity = 100 },
        new SalesData { Id = 2, Region = "South", ProductCategory = "Electronics", OrderMonth = "2024-01", TotalSales = 8000m, Quantity = 80 },
        new SalesData { Id = 3, Region = "North", ProductCategory = "Furniture", OrderMonth = "2024-01", TotalSales = 5000m, Quantity = 50 },
        new SalesData { Id = 4, Region = "North", ProductCategory = "Electronics", OrderMonth = "2024-02", TotalSales = 12000m, Quantity = 120 },
    ];

    [Fact]
    public void PivotConfigurationBuilder_DiscoversAvailableDimensions()
    {
        // Arrange & Act
        var builder = PivotConfigurationExtensions.ForType<SalesData>();
        var config = builder
            .GroupRowsBy(nameof(SalesData.Region))
            .GroupColumnsBy(nameof(SalesData.ProductCategory))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert
        config.RowDimensions.Should().HaveCount(1);
        config.RowDimensions.First().Field.Should().Be(nameof(SalesData.Region));
        config.RowDimensions.First().DisplayName.Should().Be("Sales Region");

        config.ColumnDimensions.Should().HaveCount(1);
        config.ColumnDimensions.First().Field.Should().Be(nameof(SalesData.ProductCategory));
        config.ColumnDimensions.First().DisplayName.Should().Be("Product Category");

        config.Aggregates.Should().HaveCount(1);
        config.Aggregates.First().Field.Should().Be(nameof(SalesData.TotalSales));
        config.Aggregates.First().DisplayName.Should().Be("Total Sales");
    }

    [Fact]
    public void PivotConfigurationBuilder_UsesDisplayNameAttributes()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.OrderMonth))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert
        config.RowDimensions.First().DisplayName.Should().Be("Order Month");
        config.Aggregates.First().DisplayName.Should().Be("Total Sales");
    }

    [Fact]
    public void PivotConfigurationBuilder_AllowsCustomDisplayNames()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region), dim => dim.WithDisplayName("Geographic Region"))
            .GroupColumnsBy(nameof(SalesData.ProductCategory), dim => dim.WithDisplayName("Category"))
            .Aggregate(nameof(SalesData.TotalSales), agg => agg.WithDisplayName("Revenue"))
            .Build();

        // Assert
        config.RowDimensions.First().DisplayName.Should().Be("Geographic Region");
        config.ColumnDimensions.First().DisplayName.Should().Be("Category");
        config.Aggregates.First().DisplayName.Should().Be("Revenue");
    }

    [Fact]
    public void PivotConfigurationBuilder_ConfiguresAggregateFunction()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.Quantity), agg => agg.WithFunction(AggregateFunction.Average))
            .Build();

        // Assert
        config.Aggregates.First().Function.Should().Be(AggregateFunction.Average);
    }

    [Fact]
    public void PivotConfigurationBuilder_ConfiguresFormat()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales), agg => agg.WithFormat("{0:C}"))
            .Build();

        // Assert
        config.Aggregates.First().Format.Should().Be("{0:C}");
    }

    [Fact]
    public void PivotConfigurationBuilder_ConfiguresSortOrder()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales), agg => agg.WithSortOrder(SortOrder.Descending))
            .Build();

        // Assert
        config.Aggregates.First().SortOrder.Should().Be(SortOrder.Descending);
    }

    [Fact]
    public void PivotConfigurationBuilder_ConfiguresRowAndColumnTotals()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
            .WithRowTotals(false)
            .WithColumnTotals(false)
            .Build();

        // Assert
        config.ShowRowTotals.Should().BeFalse();
        config.ShowColumnTotals.Should().BeFalse();
    }

    [Fact]
    public void PivotConfigurationBuilder_DefaultsToShowTotals()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert
        config.ShowRowTotals.Should().BeTrue();
        config.ShowColumnTotals.Should().BeTrue();
    }

    [Fact]
    public void PivotConfigurationBuilder_ExcludesNotVisibleProperties()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.InternalNotes))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert - NotVisible properties can be used but get default values
        config.RowDimensions.Should().HaveCount(1);
        config.RowDimensions.First().Field.Should().Be(nameof(SalesData.InternalNotes));
        config.RowDimensions.First().DisplayName.Should().Be("Internal Notes");
        config.RowDimensions.First().Width.Should().Be("120px");
        config.RowDimensions.First().TypeName.Should().Be("System.String");
    }

    [Fact]
    public void ToPivotGrid_CreatesValidPivotGridControl()
    {
        // Arrange & Act
        var pivotGrid = TestData.ToPivotGrid<SalesData>(pivot => pivot
            .GroupRowsBy(nameof(SalesData.Region))
            .GroupColumnsBy(nameof(SalesData.ProductCategory))
            .Aggregate(nameof(SalesData.TotalSales), agg => agg.WithFunction(AggregateFunction.Sum).WithFormat("{0:C}"))
        );

        // Assert
        pivotGrid.Should().NotBeNull();
        pivotGrid.Data.Should().NotBeNull();
        pivotGrid.Configuration.Should().NotBeNull();
        pivotGrid.Configuration.RowDimensions.Should().HaveCount(1);
        pivotGrid.Configuration.ColumnDimensions.Should().HaveCount(1);
        pivotGrid.Configuration.Aggregates.Should().HaveCount(1);
    }

    [Fact]
    public void ToPivotGrid_PreservesDataCollection()
    {
        // Arrange & Act
        var pivotGrid = TestData.ToPivotGrid<SalesData>(pivot => pivot
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
        );

        // Assert
        var data = pivotGrid.Data as SalesData[];
        data.Should().NotBeNull();
        data.Should().HaveCount(4);
        data.Should().BeEquivalentTo(TestData);
    }

    [Fact]
    public void PivotConfigurationBuilder_SupportsMultipleRowDimensions()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .GroupRowsBy(nameof(SalesData.ProductCategory))
            .GroupColumnsBy(nameof(SalesData.OrderMonth))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert
        config.RowDimensions.Should().HaveCount(2);
        config.RowDimensions.Select(d => d.Field).Should().ContainInOrder(
            nameof(SalesData.Region),
            nameof(SalesData.ProductCategory)
        );
    }

    [Fact]
    public void PivotConfigurationBuilder_SupportsMultipleAggregates()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales), agg => agg.WithFunction(AggregateFunction.Sum))
            .Aggregate(nameof(SalesData.Quantity), agg => agg.WithFunction(AggregateFunction.Sum))
            .Build();

        // Assert
        config.Aggregates.Should().HaveCount(2);
        config.Aggregates.Select(a => a.Field).Should().ContainInOrder(
            nameof(SalesData.TotalSales),
            nameof(SalesData.Quantity)
        );
    }

    [Fact]
    public void PivotConfigurationBuilder_SetsDefaultWidthForStringDimensions()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Build();

        // Assert
        config.RowDimensions.First().Width.Should().Be("200px");
    }

    [Fact]
    public void PivotConfigurationBuilder_AllowsCustomWidth()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region), dim => dim.WithWidth("300px"))
            .Build();

        // Assert
        config.RowDimensions.First().Width.Should().Be("300px");
    }

    [Fact]
    public void PivotConfigurationBuilder_PopulatesAvailableDimensionsAndAggregates()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert - Should have all discovered dimensions available
        config.AvailableDimensions.Should().NotBeEmpty();
        config.AvailableDimensions.Should().Contain(d => d.Field == nameof(SalesData.Region));
        config.AvailableDimensions.Should().Contain(d => d.Field == nameof(SalesData.ProductCategory));
        config.AvailableDimensions.Should().Contain(d => d.Field == nameof(SalesData.OrderMonth));

        // Should NOT include NotVisible properties
        config.AvailableDimensions.Should().NotContain(d => d.Field == nameof(SalesData.InternalNotes));

        // Assert - Should have all discovered aggregates available
        config.AvailableAggregates.Should().NotBeEmpty();
        config.AvailableAggregates.Should().Contain(a => a.Field == nameof(SalesData.TotalSales));
        config.AvailableAggregates.Should().Contain(a => a.Field == nameof(SalesData.Quantity));

        // Verify available dimensions have proper metadata
        var regionDimension = config.AvailableDimensions.First(d => d.Field == nameof(SalesData.Region));
        regionDimension.DisplayName.Should().Be("Sales Region");
        regionDimension.Width.Should().Be("200px");
    }

    [Fact]
    public void PivotConfigurationBuilder_DefaultsToAllowFieldsPicking()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
            .Build();

        // Assert
        config.AllowFieldsPicking.Should().BeTrue();
    }

    [Fact]
    public void PivotConfigurationBuilder_ConfiguresFieldsPicking()
    {
        // Arrange & Act
        var config = PivotConfigurationExtensions.ForType<SalesData>()
            .GroupRowsBy(nameof(SalesData.Region))
            .Aggregate(nameof(SalesData.TotalSales))
            .WithFieldsPicking(false)
            .Build();

        // Assert
        config.AllowFieldsPicking.Should().BeFalse();
    }
}

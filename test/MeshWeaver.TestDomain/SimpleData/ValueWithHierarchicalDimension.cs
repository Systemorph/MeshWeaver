using System.ComponentModel.DataAnnotations;
using MeshWeaver.Arithmetics;
using MeshWeaver.Domain;

namespace MeshWeaver.TestDomain.SimpleData;

public record TestHierarchicalDimensionA : IHierarchicalDimension
{
    [Key]
    public string SystemName { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public object? Parent { get; init; }

    public static TestHierarchicalDimensionA[] Data =
    {
        new()
        {
            SystemName = "A1",
            DisplayName = "A 1",
            Parent = null
        },
        new()
        {
            SystemName = "A2",
            DisplayName = "A 2",
            Parent = null
        },
        new()
        {
            SystemName = "A11",
            DisplayName = "A 11",
            Parent = "A1"
        },
        new()
        {
            SystemName = "A12",
            DisplayName = "A 12",
            Parent = "A1"
        },
        new()
        {
            SystemName = "A111",
            DisplayName = "A 111",
            Parent = "A11"
        },
        new()
        {
            SystemName = "A112",
            DisplayName = "A 112",
            Parent = "A11"
        },
    };
}

public record TestHierarchicalDimensionB : IHierarchicalDimension
{
    [Key]
    public string SystemName { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public object? Parent { get; init; }

    public static TestHierarchicalDimensionB[] Data =
    {
        new()
        {
            SystemName = "B1",
            DisplayName = "B 1",
            Parent = null
        },
        new()
        {
            SystemName = "B2",
            DisplayName = "B 2",
            Parent = null
        },
        new()
        {
            SystemName = "B11",
            DisplayName = "B 11",
            Parent = "B1"
        },
        new()
        {
            SystemName = "B12",
            DisplayName = "B 12",
            Parent = "B1"
        },
        new()
        {
            SystemName = "B111",
            DisplayName = "B 111",
            Parent = "B11"
        },
        new()
        {
            SystemName = "B112",
            DisplayName = "B 112",
            Parent = "B11"
        },
    };
}

public record ValueWithHierarchicalDimension
{
    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionA), nameof(DimA))]
    public string DimA { get; init; } = null!;

    public double Value { get; init; }

    public static ValueWithHierarchicalDimension[] Data =
    {
        new() { DimA = "A111", Value = 1 },
        new() { DimA = "A112", Value = 2 },
        new() { DimA = "A12", Value = 3 },
        new() { DimA = "A2", Value = 4 }
    };
}

public record ValueWithAggregateByHierarchicalDimension
{
    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionA), nameof(DimA))]
    [AggregateBy]
    public string DimA { get; init; } = null!;

    public double Value { get; init; }

    public static ValueWithAggregateByHierarchicalDimension[] Data =
    {
        new() { DimA = "A111", Value = 1 },
        new() { DimA = "A112", Value = 2 },
        new() { DimA = "A12", Value = 3 },
        new() { DimA = "A2", Value = 4 }
    };
}

public record ValueWithTwoHierarchicalDimensions
{
    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionA), nameof(DimA))]
    public string DimA { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionB), nameof(DimB))]
    public string DimB { get; init; } = null!;

    public double Value { get; init; }

    public static ValueWithTwoHierarchicalDimensions[] Data =
    {
        new()
        {
            DimA = "A111",
            DimB = "B2",
            Value = 1
        },
        new()
        {
            DimA = "A112",
            DimB = "B11",
            Value = 2
        },
        new()
        {
            DimA = "A111",
            DimB = "B12",
            Value = 3
        },
        new()
        {
            DimA = "A112",
            DimB = "B1",
            Value = 4
        }
    };
}

public record ValueWithMixedDimensions
{
    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionA), nameof(DimA))]
    public string DimA { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(string), nameof(DimD))]
    public string DimD { get; init; } = null!;

    public double Value { get; init; }

    public static ValueWithMixedDimensions[] Data =
    {
        new()
        {
            DimA = "A111",
            DimD = "D1",
            Value = 1
        },
        new()
        {
            DimA = "A112",
            DimD = "D1",
            Value = 2
        },
        new()
        {
            DimA = "A111",
            DimD = "D2",
            Value = 3
        },
        new()
        {
            DimA = "A112",
            DimD = "D3",
            Value = 4
        }
    };
}

public record ValueWithTwoAggregateByHierarchicalDimensions
{
    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionA), nameof(DimA))]
    [AggregateBy]
    public string DimA { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(TestHierarchicalDimensionB), nameof(DimB))]
    [AggregateBy]
    public string DimB { get; init; } = null!;

    public double Value { get; init; }

    public static ValueWithTwoAggregateByHierarchicalDimensions[] Data =
    {
        new()
        {
            DimA = "A111",
            DimB = "B2",
            Value = 1
        },
        new()
        {
            DimA = "A112",
            DimB = "B11",
            Value = 2
        },
        new()
        {
            DimA = "A111",
            DimB = "B12",
            Value = 3
        },
        new()
        {
            DimA = "A112",
            DimB = "B1",
            Value = 4
        }
    };
}

public record ValueWithLevelDimensions
{
    [NotVisible]
    [Dimension(typeof(string), nameof(Level1))]
    public string Level1 { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(string), nameof(Level2))]
    public string? Level2 { get; init; }

    [NotVisible]
    [Dimension(typeof(string), nameof(Level3))]
    public string? Level3 { get; init; }

    public double Value { get; init; }

    public static ValueWithLevelDimensions[] Data =
    {
        new()
        {
            Level1 = "A1",
            Level2 = "A11",
            Level3 = "A111",
            Value = 1
        },
        new()
        {
            Level1 = "A1",
            Level2 = "A11",
            Level3 = "A112",
            Value = 2
        },
        new()
        {
            Level1 = "A1",
            Level2 = "A12",
            Level3 = null,
            Value = 3
        },
        new()
        {
            Level1 = "A2",
            Level2 = null,
            Level3 = null,
            Value = 4
        }
    };
}

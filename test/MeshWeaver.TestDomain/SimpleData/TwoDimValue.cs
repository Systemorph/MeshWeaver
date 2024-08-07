using MeshWeaver.Domain;

namespace MeshWeaver.TestDomain.SimpleData;

public record Dim1 : Dimension { }

public record Dim2 : Dimension { }

public record TwoDimValue
{
    [NotVisible]
    [Dimension(typeof(Dim1))]
    public string Dim1 { get; init; }

    [NotVisible]
    [Dimension(typeof(Dim2))]
    public string Dim2 { get; init; }

    public double Value { get; init; }

    public static TwoDimValue[] Data =
    {
        new()
        {
            Dim1 = null,
            Dim2 = "A",
            Value = 1
        },
        new()
        {
            Dim1 = null,
            Dim2 = "B",
            Value = 2
        },
        new()
        {
            Dim1 = "a",
            Dim2 = "A",
            Value = 3
        },
        new()
        {
            Dim1 = "a",
            Dim2 = "B",
            Value = 4
        },
        new()
        {
            Dim1 = "c",
            Dim2 = "C",
            Value = 5
        },
        new()
        {
            Dim1 = "c",
            Dim2 = "D",
            Value = 6
        }
    };
}

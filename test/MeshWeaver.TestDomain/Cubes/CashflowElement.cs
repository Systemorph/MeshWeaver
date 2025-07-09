using MeshWeaver.Arithmetics;
using MeshWeaver.Domain;
using static MeshWeaver.Arithmetics.ArithmeticOperations;

namespace MeshWeaver.TestDomain.Cubes;

public record CashflowElementWithInt : CashflowElement
{
    [NotVisible]
    [Dimension(typeof(int), nameof(Year))]
    [IdentityProperty]
    public int Year { get; init; }
}

// TODO: Should DimensionAttribute inherit from IdentityPropertyAttribute? (2021/05/19, Roland Buergi)
public record CashflowElement : IHasValue
{
    [NotVisible]
    [Dimension(typeof(LineOfBusiness))]
    [IdentityProperty]
    public string LineOfBusiness { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(Country))]
    [IdentityProperty]
    public string Country { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(AmountType))]
    [IdentityProperty]
    public string AmountType { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(Scenario))]
    [IdentityProperty]
    public string Scenario { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(Split))]
    [IdentityProperty]
    public string Split { get; init; } = null!;

    [NotVisible]
    [Dimension(typeof(Currency))]
    [IdentityProperty]
    [AggregateBy]
    public string Currency { get; init; } = null!;

    public double Value { get; init; }

    public static CashflowElement operator +(CashflowElement a, CashflowElement b) => Sum(a, b);

    public static CashflowElement operator +(CashflowElement a, double b) => Sum(a, b);

    public static CashflowElement operator +(double a, CashflowElement b) => Sum(a, b);

    public static CashflowElement operator -(CashflowElement a, CashflowElement b) =>
        Subtract(a, b);

    public static CashflowElement operator -(CashflowElement a, double b) => Subtract(a, b);

    public static CashflowElement operator *(double a, CashflowElement b) => Multiply(a, b);

    public static CashflowElement operator *(CashflowElement a, double b) => Multiply(a, b);

    public static CashflowElement operator /(CashflowElement a, double b) => Divide(a, b);

    public static CashflowElement operator ^(CashflowElement a, double b) => Power(a, b);
}

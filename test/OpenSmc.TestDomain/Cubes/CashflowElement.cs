using OpenSmc.Arithmetics;
using OpenSmc.Domain;
using static OpenSmc.Arithmetics.ArithmeticOperations;

namespace OpenSmc.TestDomain.Cubes;

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
    public string LineOfBusiness { get; init; }

    [NotVisible]
    [Dimension(typeof(Country))]
    [IdentityProperty]
    public string Country { get; init; }

    [NotVisible]
    [Dimension(typeof(AmountType))]
    [IdentityProperty]
    public string AmountType { get; init; }

    [NotVisible]
    [Dimension(typeof(Scenario))]
    [IdentityProperty]
    public string Scenario { get; init; }

    [NotVisible]
    [Dimension(typeof(Split))]
    [IdentityProperty]
    public string Split { get; init; }

    [NotVisible]
    [Dimension(typeof(Currency))]
    [IdentityProperty]
    [AggregateBy]
    public string Currency { get; init; }

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

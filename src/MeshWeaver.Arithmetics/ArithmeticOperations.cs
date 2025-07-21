#nullable enable
using MeshWeaver.Arithmetics.Aggregation;
using MeshWeaver.Arithmetics.MapOver;

namespace MeshWeaver.Arithmetics;

public static class ArithmeticOperations
{
    public static T Sum<T>(T a, T b)
    {
        return SumFunction.Sum(a, b);
    }

    public static T Sum<T>(T a, params T[] bs)
    {
        if (bs == null)
            throw new ArgumentNullException(nameof(bs));

        return bs.Aggregate(a, SumFunction.Sum);
    }

    public static T Sum<T>(double a, T b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Plus)(a, b);
    }

    public static T Sum<T>(T a, double b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Plus)(b, a);
    }

    public static T Subtract<T>(T a, T b)
    {
        return Sum(a, Multiply(-1, b));
    }

    public static T Subtract<T>(double a, T b)
    {
        return Sum(a, Multiply(-1, b));
    }

    public static T Subtract<T>(T a, double b)
    {
        return Sum(a, -b);
    }

    public static T Multiply<T>(double a, T b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Scale)(a, b);
    }

    public static T Multiply<T>(T a, double b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Scale)(b, a);
    }

    public static T Divide<T>(T a, double b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Scale)(1.0 / b, a);
    }

    public static T Power<T>(T a, double b)
    {
        return MapOverFields.GetMapOver<T>(ArithmeticOperation.Power)(b, a);
    }
}
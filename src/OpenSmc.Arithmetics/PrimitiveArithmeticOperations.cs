using System.Diagnostics.CodeAnalysis;

namespace OpenSmc.Arithmetics;

/// <remarks>
/// These a workarounds for primitive types. DO NOT remove unless the whole MapOver stuff supports these
/// </remarks>
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class PrimitiveArithmeticOperations
{
    public static string Sum(string a, string b) => a + b;

    public static int Sum(int a, int b) => a + b;
    public static long Sum(long a, long b) => a + b;
    public static double Sum(double a, double b) => a + b;
    public static float Sum(float a, float b) => a + b;
    public static decimal Sum(decimal a, decimal b) => a + b;

    public static long Sum(int a, long b) => a + b;
    public static long Sum(long a, int b) => a + b;
    public static double Sum(int a, double b) => a + b;
    public static double Sum(double a, int b) => a + b;
    public static float Sum(int a, float b) => a + b;
    public static float Sum(float a, int b) => a + b;
    public static decimal Sum(int a, decimal b) => a + b;
    public static decimal Sum(decimal a, int b) => a + b;

    public static double Sum(long a, double b) => a + b;
    public static double Sum(double a, long b) => a + b;
    public static float Sum(long a, float b) => a + b;
    public static float Sum(float a, long b) => a + b;
    public static decimal Sum(long a, decimal b) => a + b;
    public static decimal Sum(decimal a, long b) => a + b;

    public static double Sum(double a, float b) => a + b;
    public static double Sum(float a, double b) => a + b;

    private const string CannotApplySumOperatorToOperandsOfTypeDecimalAndDouble = "Cannot apply operator '+' to operands of type 'decimal' and 'double'";

    [Obsolete(CannotApplySumOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Sum(double a, decimal b) => throw new ArgumentException(CannotApplySumOperatorToOperandsOfTypeDecimalAndDouble);

    [Obsolete(CannotApplySumOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Sum(decimal a, double b) => throw new ArgumentException(CannotApplySumOperatorToOperandsOfTypeDecimalAndDouble);

    private const string CannotApplySumOperatorToOperandsOfTypeDecimalAndFloat = "Cannot apply operator '+' to operands of type 'decimal' and 'float'";

    [Obsolete(CannotApplySumOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Sum(float a, decimal b) => throw new ArgumentException(CannotApplySumOperatorToOperandsOfTypeDecimalAndFloat);

    [Obsolete(CannotApplySumOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Sum(decimal a, float b) => throw new ArgumentException(CannotApplySumOperatorToOperandsOfTypeDecimalAndFloat);

    public static int Subtract(int a, int b) => a - b;
    public static long Subtract(long a, long b) => a - b;
    public static double Subtract(double a, double b) => a - b;
    public static float Subtract(float a, float b) => a - b;
    public static decimal Subtract(decimal a, decimal b) => a - b;

    public static long Subtract(int a, long b) => a - b;
    public static long Subtract(long a, int b) => a - b;
    public static double Subtract(int a, double b) => a - b;
    public static double Subtract(double a, int b) => a - b;
    public static float Subtract(int a, float b) => a - b;
    public static float Subtract(float a, int b) => a - b;
    public static decimal Subtract(int a, decimal b) => a - b;
    public static decimal Subtract(decimal a, int b) => a - b;

    public static double Subtract(long a, double b) => a - b;
    public static double Subtract(double a, long b) => a - b;
    public static float Subtract(long a, float b) => a - b;
    public static float Subtract(float a, long b) => a - b;
    public static decimal Subtract(long a, decimal b) => a - b;
    public static decimal Subtract(decimal a, long b) => a - b;

    public static double Subtract(double a, float b) => a - b;
    public static double Subtract(float a, double b) => a - b;

    private const string CannotApplySubtractOperatorToOperandsOfTypeDecimalAndDouble = "Cannot apply operator '-' to operands of type 'decimal' and 'double'";

    [Obsolete(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Subtract(double a, decimal b) => throw new ArgumentException(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndDouble);

    [Obsolete(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Subtract(decimal a, double b) => throw new ArgumentException(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndDouble);

    private const string CannotApplySubtractOperatorToOperandsOfTypeDecimalAndFloat = "Cannot apply operator '-' to operands of type 'decimal' and 'float'";

    [Obsolete(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Subtract(float a, decimal b) => throw new ArgumentException(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndFloat);

    [Obsolete(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Subtract(decimal a, float b) => throw new ArgumentException(CannotApplySubtractOperatorToOperandsOfTypeDecimalAndFloat);

    public static int Multiply(int a, int b) => a * b;
    public static long Multiply(long a, long b) => a * b;
    public static double Multiply(double a, double b) => a * b;
    public static float Multiply(float a, float b) => a * b;
    public static decimal Multiply(decimal a, decimal b) => a * b;

    public static long Multiply(int a, long b) => a * b;
    public static long Multiply(long a, int b) => a * b;
    public static double Multiply(int a, double b) => a * b;
    public static double Multiply(double a, int b) => a * b;
    public static float Multiply(int a, float b) => a * b;
    public static float Multiply(float a, int b) => a * b;
    public static decimal Multiply(int a, decimal b) => a * b;
    public static decimal Multiply(decimal a, int b) => a * b;

    public static double Multiply(long a, double b) => a * b;
    public static double Multiply(double a, long b) => a * b;
    public static float Multiply(long a, float b) => a * b;
    public static float Multiply(float a, long b) => a * b;
    public static decimal Multiply(long a, decimal b) => a * b;
    public static decimal Multiply(decimal a, long b) => a * b;

    public static double Multiply(double a, float b) => a * b;
    public static double Multiply(float a, double b) => a * b;

    private const string CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndDouble = "Cannot apply operator '*' to operands of type 'decimal' and 'double'";

    [Obsolete(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Multiply(double a, decimal b) => throw new ArgumentException(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndDouble);

    [Obsolete(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Multiply(decimal a, double b) => throw new ArgumentException(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndDouble);

    private const string CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndFloat = "Cannot apply operator '*' to operands of type 'decimal' and 'float'";

    [Obsolete(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Multiply(float a, decimal b) => throw new ArgumentException(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndFloat);

    [Obsolete(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Multiply(decimal a, float b) => throw new ArgumentException(CannotApplyMultiplyOperatorToOperandsOfTypeDecimalAndFloat);

    public static int Divide(int a, int b) => a / b;
    public static long Divide(long a, long b) => a / b;
    public static double Divide(double a, double b) => a / b;
    public static float Divide(float a, float b) => a / b;
    public static decimal Divide(decimal a, decimal b) => a / b;

    public static long Divide(int a, long b) => a / b;
    public static long Divide(long a, int b) => a / b;
    public static double Divide(int a, double b) => a / b;
    public static double Divide(double a, int b) => a / b;
    public static float Divide(int a, float b) => a / b;
    public static float Divide(float a, int b) => a / b;
    public static decimal Divide(int a, decimal b) => a / b;
    public static decimal Divide(decimal a, int b) => a / b;

    public static double Divide(long a, double b) => a / b;
    public static double Divide(double a, long b) => a / b;
    public static float Divide(long a, float b) => a / b;
    public static float Divide(float a, long b) => a / b;
    public static decimal Divide(long a, decimal b) => a / b;
    public static decimal Divide(decimal a, long b) => a / b;

    public static double Divide(double a, float b) => a / b;
    public static double Divide(float a, double b) => a / b;

    private const string CannotApplyDivideOperatorToOperandsOfTypeDecimalAndDouble = "Cannot apply operator '/' to operands of type 'decimal' and 'double'";

    [Obsolete(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Divide(double a, decimal b) => throw new ArgumentException(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndDouble);

    [Obsolete(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndDouble, true)]
    public static void Divide(decimal a, double b) => throw new ArgumentException(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndDouble);

    private const string CannotApplyDivideOperatorToOperandsOfTypeDecimalAndFloat = "Cannot apply operator '/' to operands of type 'decimal' and 'float'";

    [Obsolete(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Divide(float a, decimal b) => throw new ArgumentException(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndFloat);

    [Obsolete(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndFloat, true)]
    public static void Divide(decimal a, float b) => throw new ArgumentException(CannotApplyDivideOperatorToOperandsOfTypeDecimalAndFloat);

    public static double Power(int a, double b) => Math.Pow(a, b);
    public static double Power(long a, double b) => Math.Pow(a, b);
    public static double Power(double a, double b) => Math.Pow(a, b);
    public static double Power(float a, double b) => Math.Pow(a, b);

    private const string CannotApplyPowerOperatorToOperandOfTypeDecimal = "Cannot calculate power of operand of type 'decimal'";
    
    [Obsolete(CannotApplyPowerOperatorToOperandOfTypeDecimal, true)]
    public static void Power(decimal a, double b) => throw new ArgumentException(CannotApplyPowerOperatorToOperandOfTypeDecimal);
}
namespace MeshWeaver.Arithmetics
{
    /// <summary>
    ///  Do not apply arithmetic operations such as addition and subtraction on objects.
    /// </summary>
    /// <conceptualLink target="799cb1b4-2638-49fb-827a-43131d364f06" />
    /// <conceptualLink target="3b557ccf-d392-496f-933b-08672b0e2d02#mapOverFields" />
    // TODO V10: Inherit NotAggregateAttribute from NoArithmeticsAttribute(ArithmeticOperation.Plus) (2021/05/28, Roland Buergi)
    [AttributeUsage(AttributeTargets.Property)]
    public class NoArithmeticsAttribute : Attribute
    {
        public ArithmeticOperation Operations { get; }

        public NoArithmeticsAttribute(ArithmeticOperation operations = ArithmeticOperation.Plus | ArithmeticOperation.Power | ArithmeticOperation.Scale)
        {
            Operations = operations;
        }
    }
}
namespace OpenSmc.Arithmetics.MapOver
{
    public class IsSupportedValueTypeFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForValueType(type, method);
    }

    public class IsDictionaryFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForDictionary(type, method);
    }

    public class IsClassHasParameterlessConstructorFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForClass(type, method);
    }

    public class IsArrayFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForArray(type, method);
    }


    public class IsListFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForList(type, method);
    }

    public class IsEnumerableFunctionProvider : IMapOverFunctionProvider
    {
        public Delegate GetDelegate(Type type, ArithmeticOperation method) =>
            MapOverFields.CreateMapOverDelegateForEnumerable(type, method);
    }
}

namespace OpenSmc.Arithmetics;

public interface IMapOverFunctionProvider
{
    Delegate GetDelegate(Type type, ArithmeticOperation method);
}
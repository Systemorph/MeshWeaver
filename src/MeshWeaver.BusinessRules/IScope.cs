namespace MeshWeaver.BusinessRules;

public interface IScope
{
    TScope GetScope<TScope>(object identity) where TScope : IScope;

}
public interface IScope<out TIdentity, out TState> : IScope
{
    TIdentity Identity { get; }
    TState GetStorage();
}

using System.Reflection;
using MeshWeaver.Scopes.Proxy;

namespace MeshWeaver.Scopes.Synchronization;

public interface IInternalMutableScope : IMutableScope
{
    event ScopeInvalidatedHandler ScopeInvalidated;

    Task<EvaluateWithDependenciesResult> EvaluateWithDependenciesAsync(Func<Task<object>> expression);

    void Invalidate(IReadOnlyCollection<MethodInfo> methods, ICollection<(object, IInternalMutableScope)> notifications);
    IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> GetDependencies(MethodInfo method);
    IScopeRegistry GetScopeRegistry();
    object GetScopeById(Guid id);

    void Refresh();
}

public record EvaluateWithDependenciesResult(object Result, IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> Dependencies, ExpressionChangedStatus Status);
public enum ExpressionChangedStatus { Success, Error, Evaluating }

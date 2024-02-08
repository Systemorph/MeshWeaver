using System.Reflection;
using OpenSmc.Messaging;
using OpenSmc.Scopes;

namespace OpenSmc.Application.Scope;


public record ExpressionRegistryItem(string Id, Func<Task<object>> GenerationFunction, EvaluationSubscriptionOptions Options, IMessageDelivery Request):IDisposable
{
    public Dictionary<object, Dictionary<string, (IMutableScope Scope, PropertyInfo Property)[]>> Dependencies { get; set; }
    public object Value { get; set; }
    public readonly List<Action> DisposeActions = new();


    public void Dispose()
    {
        foreach (var disposable in DisposeActions)
        {
            disposable();
        }
    }
}
using System.Reflection;
using Castle.DynamicProxy;
using OpenSmc.Conventions;

namespace OpenSmc.Scopes.Proxy
{
    public delegate bool AspectPredicate(MethodInfo method);

    public interface IScopeInterceptor : IInterceptor
    {
        IEnumerable<AspectPredicate> Predicates { get; }
    }


    public abstract class ScopeInterceptorBase : IScopeInterceptor
    {
        private static readonly AspectPredicate[] AspectPredicates = { _ => true };

        public virtual IEnumerable<AspectPredicate> Predicates => AspectPredicates;
        public abstract void Intercept(IInvocation invocation);
    }
}
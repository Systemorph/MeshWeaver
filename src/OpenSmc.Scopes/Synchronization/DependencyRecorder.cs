using System.Reflection;
using OpenSmc.Reflection;

namespace OpenSmc.Scopes.Synchronization
{
    internal interface IDependencyRecorder : IDisposable
    {
        IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> Dependencies { get; }
    }


    internal class DependencyRecorder : IDependencyRecorder
    {
        private readonly Action<DependencyRecorder> disposeAction;

        public DependencyRecorder(Action<DependencyRecorder> disposeAction)
        {
            this.disposeAction = disposeAction;
        }

        public void Dispose()
        {
            disposeAction(this);
        }

        internal void AddDependency((IInternalMutableScope Scope, MethodInfo Method) dependency)
        {
            dependenciesByGetter.Add(dependency);
        }

        private readonly HashSet<(IInternalMutableScope Scope, MethodInfo Method)> dependenciesByGetter = new();
        public IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> Dependencies => ComputeDependencies();

        private IReadOnlyCollection<(IMutableScope Scope, PropertyInfo Property)> ComputeDependencies()
        {
            var allGetters = new HashSet<(IInternalMutableScope Scope, MethodInfo Method)>();
            AddDependencies(dependenciesByGetter, allGetters);

            var ret = new List<(IMutableScope Scope, PropertyInfo Property)>();
            foreach (var (scope, method) in allGetters)
            {
                var property = method.GetProperty();
                if (property.SetMethod != null)
                    ret.Add((scope,property));
            }

            return ret;
        }

        private void AddDependencies(IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> dependencies, HashSet<(IInternalMutableScope Scope, MethodInfo Method)> allGetters)
        {
            // HACK V10: dependencies.ToArray should not be here. It is added to prevent collection modification outside. (2023/09/06, Alexander Yolokhov)
            foreach (var dependency in dependencies.ToArray())
                if (allGetters.Add(dependency))
                {
                    AddDependencies(dependency.Scope.GetDependencies(dependency.Method), allGetters);
                }
        }
    }
}
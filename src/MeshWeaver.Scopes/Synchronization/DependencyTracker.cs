using System.Reflection;

namespace MeshWeaver.Scopes.Synchronization
{
    internal class DependencyTracker
    {
        private class DependencyNode
        {
            public (IInternalMutableScope scope, MethodInfo method) Node { get; }
            public (IInternalMutableScope Scope, MethodInfo Method) Caller { get; }
            public readonly HashSet<(IInternalMutableScope Scope, MethodInfo Method)> Dependencies = new();

            public DependencyNode((IInternalMutableScope scope, MethodInfo method) node, (IInternalMutableScope Scope, MethodInfo Method) caller)
            {
                Node = node;
                Caller = caller;
            }
        }

        private List<(IInternalMutableScope Scope, MethodInfo Method)> Invalidations { get; } = new();

        //private List<(IMutableScope Scope, MethodInfo Method)> Dependencies { get; } = new();
        private Dictionary<(IInternalMutableScope Scope, MethodInfo Method), DependencyNode> DependencyNodes { get; } = new();
        private DependencyNode Current { get; set; }

        public void AddGetter(IInternalMutableScope scope, MethodInfo method)
        {
            var key = (scope, method);
            Invalidations.Add(key);
            Current?.Dependencies.Add(key);
            var dependencyNode = new DependencyNode(key, Current?.Node ?? default);
            DependencyNodes[key] = dependencyNode;
            Current = dependencyNode;
        }

        public IEnumerable<(IInternalMutableScope scope, MethodInfo method)> GetInvalidations()
        {
            for (int i = 0; i < Invalidations.Count - 1; i++)
            {
                var element = Invalidations[i];
                yield return element;
            }
        }

        public IEnumerable<(IInternalMutableScope Scope, MethodInfo Method)> GetDependencies()
        {
            var key = Current.Node;
            if (DependencyNodes.TryGetValue(key, out var node))
                return node.Dependencies;
            return Enumerable.Empty<(IInternalMutableScope Scope, MethodInfo Method)>();
        }

        public void Pop()
        {
            Invalidations.RemoveAt(Invalidations.Count - 1);
            Current = Current.Caller == default ? null : DependencyNodes[Current.Caller];
        }
    }
}
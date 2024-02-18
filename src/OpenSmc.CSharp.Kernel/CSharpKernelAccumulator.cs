using System.Collections.ObjectModel;
using OpenSmc.DotNet.Kernel;

namespace OpenSmc.CSharp.Kernel
{
    public class CSharpKernelAccumulator : IDotNetKernel
    {
        private readonly HashSet<string> usings = new();
        public IReadOnlyCollection<string> Usings => usings;

        public void AddUsings(params string[] namespaces)
        {
            if (namespaces == null)
                throw new ArgumentNullException(nameof(namespaces));

            usings.UnionWith(namespaces);
        }

        private readonly HashSet<string> usingsStatic = new();

        public IReadOnlyCollection<string> UsingsStatic => usingsStatic;

        public void AddUsingsStatic(params string[] staticNamespaces)
        {
            if (staticNamespaces == null)
                throw new ArgumentNullException(nameof(staticNamespaces));

            usingsStatic.UnionWith(staticNamespaces);
        }


        private readonly Dictionary<string, Type> usingsTypes = new();

        public ReadOnlyDictionary<string, Type> UsingsTypes => new(usingsTypes);

        public void AddUsingType<T>(string name = null)
        {
            var usingsType = typeof(T);
            name ??= usingsType.Name;

            usingsTypes[name] = usingsType;
        }
    }
}
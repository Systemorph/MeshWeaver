using System.Linq.Expressions;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

namespace OpenSmc.CSharp.Roslyn
{
    public static class RoslynHelpers
    {
        private static readonly Func<InteractiveAssemblyLoader, Stream, Stream, Assembly> LoadAssemblyFromStreamFunc = BuildAssemblyFromStreamFunc();
        private static readonly Func<CSharpCompilationOptions, CSharpCompilationOptions> WithTopLevelBinderFlagsForScriptFunc = BuildWithTopLevelBinderFlagsForScriptFunc();

        private static Func<CSharpCompilationOptions, CSharpCompilationOptions> BuildWithTopLevelBinderFlagsForScriptFunc()
        {
            var method = typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
            // ReSharper disable once AssignNullToNotNullAttribute
            // ReSharper disable once PossibleNullReferenceException
            var flagsType = method.GetParameters().First().ParameterType;
            var prm = Expression.Parameter(typeof(CSharpCompilationOptions));
            return Expression.Lambda<Func<CSharpCompilationOptions, CSharpCompilationOptions>>(Expression.Call(prm, method, Expression.Constant(Enum.Parse(flagsType, "IgnoreCorLibraryDuplicatedTypes"))), prm).Compile();

        }

        private static Func<InteractiveAssemblyLoader, Stream, Stream, Assembly> BuildAssemblyFromStreamFunc()
        {
            var loadAssemblyFromStreamMethod = typeof(InteractiveAssemblyLoader).GetMethod("LoadAssemblyFromStream", BindingFlags.Instance | BindingFlags.NonPublic);
            var prm1 = Expression.Parameter(typeof(Stream));
            var prm2 = Expression.Parameter(typeof(Stream));
            var loader = Expression.Parameter(typeof(InteractiveAssemblyLoader));
            // ReSharper disable once AssignNullToNotNullAttribute
            return Expression.Lambda<Func<InteractiveAssemblyLoader, Stream, Stream, Assembly>>(Expression.Call(loader, loadAssemblyFromStreamMethod, prm1, prm2), loader, prm1, prm2).Compile();
        }


        public static Assembly LoadAssemblyFromStream(this InteractiveAssemblyLoader loader, Stream peStream, Stream pdbStream)
        {
            return LoadAssemblyFromStreamFunc(loader, peStream, pdbStream);
        }

        public static CSharpCompilationOptions WithTopLevelBinderFlagsForScript(this CSharpCompilationOptions options)
        {
            return WithTopLevelBinderFlagsForScriptFunc(options);
        }

    }
}

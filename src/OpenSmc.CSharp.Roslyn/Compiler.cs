using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;

namespace OpenSmc.CSharp.Roslyn
{
    public class Compiler : ICompiler
    {
        private ICSharpSyntaxTreeService syntaxTreeService;

        public Compiler(ICSharpSyntaxTreeService syntaxTreeService)
        {
            this.syntaxTreeService = syntaxTreeService;
        }

        // ReSharper disable once InconsistentNaming
        public const string DiagnosticsAnnotationKind = nameof(DiagnosticsAnnotationKind);

        private readonly InteractiveAssemblyLoader assemblyLoader = new();

        public Compilation CreateSubmission(CSharpScript script, CSharpScriptOptions options)
        {
            var previousSubmission = script.Previous?.GetCompilation();

            return (Compilation)InnerCreateCompilationMethod.MakeGenericMethod(script.ReturnType)
                                                            .Invoke(this, new object[] { script.Code, script.Options, previousSubmission });
        }

        public Func<object[], Task<T>> CreateExecutor<T>(Compilation compilation, CSharpScriptOptions options, in CancellationToken cancellationToken)
        {
            return (Func<object[], Task<T>>)CreateExecutor((CSharpCompilation)compilation, typeof(T),
                                                           options.RoslynScriptOptions.EmitDebugInformation,
                                                           cancellationToken);
        }


        private static readonly MethodInfo InnerCreateCompilationMethod = typeof(Compiler).GetMethod(nameof(CreateCompilation));

        public CSharpCompilation CreateCompilation<T>(string expression, CSharpScriptOptions options, Compilation previousScriptCompilation)
        {
            var syntaxNode = syntaxTreeService.Parse(expression);
            var comp = syntaxTreeService.GetCompilation(syntaxNode, options ?? CSharpScriptOptions.Default, previousScriptCompilation, typeof(T));
            return comp;
        }

        private static readonly Func<object[], Task<object>> DefaultExecutor = ((s) => Task.FromResult<object>(null));

        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        public Delegate CreateExecutor(CSharpCompilation compilation, Type returnType, bool emitDebugInformation, CancellationToken cancellationToken)
        {
            var diagnostics = new List<Diagnostic>();

            // get compilation diagnostics first.
            diagnostics.AddRange(compilation.GetParseDiagnostics());
            if (HasInValidDiagnostic(diagnostics))
                throw new CSharpCompilationException("Compilation Error", diagnostics);
            diagnostics.Clear();

            var executor = Build(compilation, returnType, diagnostics, emitDebugInformation, cancellationToken);

            // emit can fail due to compilation errors or because there is nothing to emit:
            if (HasInValidDiagnostic(diagnostics))
                throw new CSharpCompilationException("Compilation Error", diagnostics);

            diagnostics.Clear();

            return executor ?? DefaultExecutor;
        }

        const string AwaitWarningId = "CS4014";

        public bool HasInValidDiagnostic(List<Diagnostic> diagnostics)
        {
            return diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error || x.Id == AwaitWarningId);
        }


        /// <summary>
        /// Builds a delegate that will execute just this scripts code.
        /// </summary>
        private Delegate Build(
            CSharpCompilation compilation,
            Type returnType,
            List<Diagnostic> diagnostics,
            bool emitDebugInformation,
            CancellationToken cancellationToken)
        {
            var entryPoint = compilation.GetEntryPoint(cancellationToken);

            using (var peStream = new MemoryStream())
            using (var pdbStreamOpt = emitDebugInformation ? new MemoryStream() : null)
            {
                var emitOptions = new EmitOptions();

                //if (emitDebugInformation)
                //{
                //    emitOptions = emitOptions.WithDebugInformationFormat(PdbHelpers.GetPlatformSpecificDebugInformationFormat());
                //}

                var emitResult = compilation.Emit(
                                                  peStream: peStream,
                                                  pdbStream: pdbStreamOpt,
                                                  xmlDocumentationStream: null,
                                                  win32Resources: null,
                                                  manifestResources: null,
                                                  options: emitOptions,
                                                  cancellationToken: cancellationToken);

                diagnostics.AddRange(emitResult.Diagnostics);

                if (!emitResult.Success)
                {
                    return null;
                }

                // let the loader know where to find assemblies:
                foreach (var referencedAssembly in compilation.References)
                {
                    var path = (referencedAssembly as PortableExecutableReference)?.FilePath;
                    if (path != null)
                    {
                        var assemblyName = AssemblyName.GetAssemblyName(path);
                        var identity = new AssemblyIdentity(assemblyName.Name, assemblyName.Version, assemblyName.CultureName,
                                                            assemblyName.GetPublicKeyToken().ToImmutableArray());

                        assemblyLoader.RegisterDependency(identity, path);
                    }
                }

                peStream.Position = 0;

                if (pdbStreamOpt != null)
                {
                    pdbStreamOpt.Position = 0;
                }

                //var libImage = ImmutableArray.Create<byte>(peStream.ToArray());
                var libAssembly = assemblyLoader.LoadAssemblyFromStream(peStream, pdbStreamOpt);

                var runtimeEntryPoint = GetEntryPointRuntimeMethod(entryPoint, libAssembly, cancellationToken);

                return runtimeEntryPoint.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(object[]), typeof(Task<>).MakeGenericType(returnType)));
            }
        }


        internal static MethodInfo GetEntryPointRuntimeMethod(IMethodSymbol entryPoint, Assembly assembly, CancellationToken cancellationToken)
        {
            string entryPointTypeName = BuildQualifiedName(entryPoint.ContainingNamespace.MetadataName, entryPoint.ContainingType.MetadataName);
            string entryPointMethodName = entryPoint.MetadataName;

            var entryPointType = assembly.GetType(entryPointTypeName, throwOnError: true, ignoreCase: false).GetTypeInfo();
            return entryPointType.GetDeclaredMethod(entryPointMethodName);
        }

        public const string DotDelimiterString = ".";

        internal static string BuildQualifiedName(
            string qualifier,
            string name)
        {
            Debug.Assert(name != null);

            if (!string.IsNullOrEmpty(qualifier))
            {
                return String.Concat(qualifier, DotDelimiterString, name);
            }

            return name;
        }

        public void Dispose()
        {
            syntaxTreeService = null;
            assemblyLoader?.Dispose();
        }
    }
}
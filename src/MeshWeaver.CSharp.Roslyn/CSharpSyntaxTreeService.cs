using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;

namespace MeshWeaver.CSharp.Roslyn
{
    public class CSharpSyntaxTreeService : ICSharpSyntaxTreeService
    {
        private static readonly CSharpParseOptions ScriptCSharpParseOptions = new(LanguageVersion.Preview, kind: SourceCodeKind.Script);

        public SyntaxTree Parse(string code)
        {
            return SyntaxFactory.ParseSyntaxTree(code, ScriptCSharpParseOptions);
        }

        public CSharpCompilation GetCompilation(SyntaxTree syntaxTree, CSharpScriptOptions scriptOptions, Compilation previousScriptCompilation, Type returnType)
        {
            return GetCompilation(syntaxTree, scriptOptions, returnType, previousScriptCompilation);
        }

        private long submissionId;
        private CSharpCompilation GetCompilation(SyntaxTree tree, CSharpScriptOptions ffScriptOptions, Type returnType, Compilation previousScriptCompilation)
        {
            var additionalMetadataReferences = ffScriptOptions.References.ToArray().Select(x => x.Value)
                                                              // x.Display is null or unique
                                                              .Where(x => x.Display == null || ffScriptOptions.RoslynScriptOptions.MetadataReferences.All(r => r.Display != x.Display));
            var scriptOptions = ffScriptOptions.RoslynScriptOptions
                                               .AddReferences(additionalMetadataReferences);

            var id = Interlocked.Increment(ref submissionId);
            tree = SyntaxFactory.ParseSyntaxTree(tree.ToString(), ScriptCSharpParseOptions);

            var references = scriptOptions.MetadataReferences.SelectMany<MetadataReference,MetadataReference>(r => r is UnresolvedMetadataReference ur ? scriptOptions.MetadataResolver.ResolveReference(ur.Reference, null, ur.Properties) : new MetadataReference[]{ r }).Distinct().ToArray();
            var cSharpCompilationOptions = GetScriptCompilationOptions(id, scriptOptions);
            var ret = CSharpCompilation.CreateScriptCompilation(
                                                                        $"A_{Guid.NewGuid().ToString()}",
                                                                        tree,
                                                                        references,
                                                                        cSharpCompilationOptions,
                                                                        (CSharpCompilation)previousScriptCompilation,
                                                                        returnType,
                                                                        null
                                                                       );

            return ret;
        }

        private CSharpCompilationOptions GetScriptCompilationOptions(long id, ScriptOptions scriptOptions)
        {
            var cSharpCompilationOptions = new CSharpCompilationOptions(
                                                                        outputKind: OutputKind.DynamicallyLinkedLibrary,
                                                                        mainTypeName: null,
                                                                        scriptClassName: $"Submission_{id}",
                                                                        usings: scriptOptions.Imports,
                                                                        optimizationLevel: OptimizationLevel.Debug, // TODO
                                                                        checkOverflow: false, // TODO
                                                                        allowUnsafe: true, // TODO
                                                                        platform: Platform.AnyCpu,
                                                                        warningLevel: 4,
                                                                        xmlReferenceResolver: null, // don't support XML file references in interactive (permissions & doc comment includes)
                                                                        sourceReferenceResolver: scriptOptions.SourceResolver,
                                                                        metadataReferenceResolver: GetMetadataReferenceResolver(scriptOptions.MetadataResolver),
                                                                        //metadataReferenceResolver: scriptOptions.MetadataResolver,
                                                                        assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default
                                                                       );
            cSharpCompilationOptions = cSharpCompilationOptions.WithTopLevelBinderFlagsForScript();
            return cSharpCompilationOptions;
        }

        private volatile MetadataReferenceResolver resolver;
        private readonly object resolverLock = new object();

        private MetadataReferenceResolver GetMetadataReferenceResolver(MetadataReferenceResolver original)
        {
            if (resolver != null)
                return resolver;
            lock (resolverLock)
            {
                if (resolver != null)
                    return resolver;
                resolver = new CachedMetadataReferenceResolver(original);
            }
            return resolver;
        }

        internal class CachedMetadataReferenceResolver : MetadataReferenceResolver
        {
            private readonly MetadataReferenceResolver original;

            public CachedMetadataReferenceResolver(MetadataReferenceResolver original)
            {
                this.original = original;
            }

            public override bool Equals(object other)
            {
                return other is CachedMetadataReferenceResolver;
            }

            public override int GetHashCode()
            {
                return 42;
            }

            private readonly ConcurrentDictionary<AssemblyIdentity, PortableExecutableReference> cache = new ConcurrentDictionary<AssemblyIdentity, PortableExecutableReference>();
            public override PortableExecutableReference ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity)
            {
                return cache.GetOrAdd(referenceIdentity, x => original.ResolveMissingAssembly(definition, referenceIdentity));
            }

            public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string baseFilePath, MetadataReferenceProperties properties) => original.ResolveReference(reference, baseFilePath, properties);
        }
    }

}

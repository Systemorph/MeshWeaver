using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;

namespace MeshWeaver.CSharp.Roslyn
{
    /// <summary>
    /// A class that represents a script that you can run.
    /// 
    /// Create a script using a language specific script class such as CSharpScript or VisualBasicScript.
    /// </summary>
    public abstract class CSharpScript : IDisposable
    {
        internal ICompiler Compiler { get; private set; }

        private Compilation lazyCompilation;

        protected IServiceProvider ServiceProvider;

        internal CSharpScript(ICompiler compiler, 
                                        IServiceProvider serviceProvider, 
                                        string code,
                                        CSharpScriptOptions options, 
                                        CSharpScript previousOpt)
        {
            Debug.Assert(options != null);
            Debug.Assert(compiler != null);
            Debug.Assert(serviceProvider != null);

            Compiler = compiler;
            ServiceProvider = serviceProvider;
            Previous = previousOpt;
            Code = code;
            Options = options;
        }


        public static CSharpScript<T> CreateInitialScript<T>(ICompiler compiler,
            IServiceProvider serviceProvider,
            string code,
            CSharpScriptOptions optionsOpt) 
        {
            return new CSharpScript<T>(compiler, serviceProvider, code,
                optionsOpt ?? CSharpScriptOptions.Interactive, null);
        }

        /// <summary>
        /// A script that will run first when this script is run. 
        /// Any declarations made in the previous script can be referenced in this script.
        /// The end state from running this script includes all declarations made by both scripts.
        /// </summary>
        public CSharpScript Previous { get; private set; }


        /// <summary>
        /// The options used by this script.
        /// </summary>
        public CSharpScriptOptions Options { get; private set; }

        /// <summary>
        /// The source code of the script.
        /// </summary>
        public string Code { get; private set; }

        /// <summary>
        /// The expected return type of the script.
        /// </summary>
        public abstract Type ReturnType { get; }

        private static readonly Func<Compilation, bool> HasReturnValueMethod = GetHasReturnValueMethod();

        private static Func<Compilation, bool> GetHasReturnValueMethod()
        {
            var method = typeof(Compilation).GetMethod("HasSubmissionResult", BindingFlags.Instance | BindingFlags.NonPublic);
            var parameter = System.Linq.Expressions.Expression.Parameter(typeof(Compilation));
            var lambda = System.Linq.Expressions.Expression.Lambda<Func<Compilation, bool>>(System.Linq.Expressions.Expression.Call(parameter, method), parameter).Compile();
            return x=> lambda(x);

        }

        public bool HasReturnValue()
        {
            return HasReturnValueMethod(GetCompilation());
        }


        /// <summary>
        /// Continues the script with given code snippet.
        /// </summary>
        public CSharpScript<T> ContinueWith<T>(string expression, CSharpScriptOptions options = null)
        {
            options = options ?? InheritOptions(Options);
            return new CSharpScript<T>(Compiler, ServiceProvider, expression, options, this);
        }

        private static CSharpScriptOptions InheritOptions(CSharpScriptOptions previous)
        {
            // don't inherit references or imports, they have already been applied:
            return previous.WithRoslynScriptOption(previous.RoslynScriptOptions.WithReferences(ImmutableArray<MetadataReference>.Empty)
                .WithImports(ImmutableArray<string>.Empty));
        }

        /// <summary>
        /// Get's the <see cref="Compilation"/> that represents the semantics of the script.
        /// </summary>
        public Compilation GetCompilation()
        {
            if (lazyCompilation == null)
            {
                var compilation = Compiler.CreateSubmission(this, Options);
                Interlocked.CompareExchange(ref lazyCompilation, compilation, null);
            }

            return lazyCompilation;
        }



        /// <summary>
        /// Runs the script from the beginning.
        /// </summary>
        /// <param name="globals">
        /// An instance of <see cref="Script.GlobalsType"/> holding on values for global variables accessible from the script.
        /// Must be specified if and only if the script was created with <see cref="Script.GlobalsType"/>.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        public Task<CSharpScriptState> RunAsync(CancellationToken cancellationToken) =>
            CommonRunAsync(null, cancellationToken);

        /// <summary>
        /// Runs the script from the beginning.
        /// </summary>
        /// <param name="globals">
        /// An instance of <see cref="Script.GlobalsType"/> holding on values for global variables accessible from the script.
        /// Must be specified if and only if the script was created with <see cref="Script.GlobalsType"/>.
        /// </param>
        /// <param name="catchException">
        /// If specified, any exception thrown by the script top-level code is passed to <paramref name="catchException"/>.
        /// If it returns true the exception is caught and stored on the resulting <see cref="ScriptState"/>, otherwise the exception is propagated to the caller.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        public Task<CSharpScriptState> RunAsync(
            Func<Exception, bool> catchException = null,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            CommonRunAsync(catchException, cancellationToken);

        internal abstract Task<CSharpScriptState> CommonRunAsync(Func<Exception, bool> catchException, CancellationToken cancellationToken);

        /// <summary>
        /// Run the script from the specified state.
        /// </summary>
        /// <param name="previousState">
        /// Previous state of the script execution.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        public Task<CSharpScriptState> RunFromAsync(CSharpScriptState previousState,
            CancellationToken cancellationToken) =>
            CommonRunFromAsync(previousState, null, cancellationToken);

        /// <summary>
        /// Run the script from the specified state.
        /// </summary>
        /// <param name="previousState">
        /// Previous state of the script execution.
        /// </param>
        /// <param name="catchException">
        /// If specified, any exception thrown by the script top-level code is passed to <paramref name="catchException"/>.
        /// If it returns true the exception is caught and stored on the resulting <see cref="ScriptState"/>, otherwise the exception is propagated to the caller.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        public Task<CSharpScriptState> RunFromAsync(CSharpScriptState previousState,
            Func<Exception, bool> catchException = null,
            CancellationToken cancellationToken = default(CancellationToken)) =>
            CommonRunFromAsync(previousState, catchException, cancellationToken);

        internal abstract Task<CSharpScriptState> CommonRunFromAsync(
            CSharpScriptState previousState, Func<Exception, bool> catchException,
            CancellationToken cancellationToken);

        /// <summary>
        /// Forces the script through the compilation step.
        /// If not called directly, the compilation step will occur on the first call to Run.
        /// </summary>
        public ImmutableArray<Diagnostic> Compile(CancellationToken cancellationToken = default(CancellationToken)) =>
            CommonCompile(cancellationToken);

        internal abstract ImmutableArray<Diagnostic> CommonCompile(CancellationToken cancellationToken);
        internal abstract Func<object[], Task> CommonGetExecutor(CancellationToken cancellationToken);

        public void Dispose()
        {
            ServiceProvider = null;
            Compiler = null;
            lazyCompilation = null;
            Options = null;
            Previous = null;
        }
    }

    public sealed class CSharpScript<T> : CSharpScript
    {


        public override Type ReturnType => typeof(T);

        internal override ImmutableArray<Diagnostic> CommonCompile(CancellationToken cancellationToken)
        {
            // TODO: avoid throwing exception, report all diagnostics https://github.com/dotnet/roslyn/issues/5949
            try
            {
                GetPrecedingExecutors(cancellationToken);
                GetExecutor(cancellationToken);

                return ImmutableArray.CreateRange(GetCompilation().GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Warning));
            }
            catch (CSharpCompilationException e)
            {
                return ImmutableArray.CreateRange(e.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning));
            }
        }

        internal override Func<object[], Task> CommonGetExecutor(CancellationToken cancellationToken)
            => GetExecutor(cancellationToken);

        
        internal override Task<CSharpScriptState> CommonRunAsync(Func<Exception, bool> catchException, CancellationToken cancellationToken) =>
            RunAsync(catchException, cancellationToken).CastAsync<CSharpScriptState<T>, CSharpScriptState>();

        internal override Task<CSharpScriptState> CommonRunFromAsync(CSharpScriptState previousState, Func<Exception, bool> catchException, CancellationToken cancellationToken) =>
            RunFromAsync(previousState, catchException, cancellationToken).CastAsync<CSharpScriptState<T>, CSharpScriptState>();



        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        private ImmutableArray<Func<object[], Task>> TryGetPrecedingExecutors(CSharpScript lastExecutedScriptInChainOpt, CancellationToken cancellationToken)
        {
            CSharpScript script = Previous;
            if (script == lastExecutedScriptInChainOpt)
            {
                return ImmutableArray<Func<object[], Task>>.Empty;
            }

            var scriptsReversed = new List<CSharpScript>();

            while (script != null && script != lastExecutedScriptInChainOpt)
            {
                scriptsReversed.Add(script);
                script = script.Previous;
            }

            if (lastExecutedScriptInChainOpt != null && script != lastExecutedScriptInChainOpt)
            {
                scriptsReversed.Clear();
                return default(ImmutableArray<Func<object[], Task>>);
            }

            var executors = new List<Func<object[], Task>>(scriptsReversed.Count);

            // We need to build executors in the order in which they are chained,
            // so that assemblies created for the submissions are loaded in the correct order.
            for (int i = scriptsReversed.Count - 1; i >= 0; i--)
            {
                executors.Add(scriptsReversed[i].CommonGetExecutor(cancellationToken));
            }

            return executors.ToImmutableArray();
        }


        /// <summary>
        /// Runs the script from the beginning.
        /// </summary>
        /// <param name="globals">
        /// An instance of <see cref="Script.GlobalsType"/> holding on values for global variables accessible from the script.
        /// Must be specified if and only if the script was created with <see cref="Script.GlobalsType"/>.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        /// <exception cref="ArgumentException">The type of <paramref name="globals"/> doesn't match <see cref="Script.GlobalsType"/>.</exception>
        public new Task<CSharpScriptState<T>> RunAsync(CancellationToken cancellationToken)
            => RunAsync(null, cancellationToken);

        /// <summary>
        /// Runs the script from the beginning.
        /// </summary>
        /// <param name="globals">
        /// An instance of <see cref="Script.GlobalsType"/> holding on values for global variables accessible from the script.
        /// Must be specified if and only if the script was created with <see cref="Script.GlobalsType"/>.
        /// </param>
        /// <param name="catchException">
        /// If specified, any exception thrown by the script top-level code is passed to <paramref name="catchException"/>.
        /// If it returns true the exception is caught and stored on the resulting <see cref="ScriptState"/>, otherwise the exception is propagated to the caller.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        /// <exception cref="ArgumentException">The type of <paramref name="globals"/> doesn't match <see cref="Script.GlobalsType"/>.</exception>
        public new Task<CSharpScriptState<T>> RunAsync(Func<Exception, bool> catchException = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The following validation and executor construction may throw;
            // do so synchronously so that the exception is not wrapped in the task.
            //ValidateGlobals(GlobalsInstance, GlobalsType);

            var executionState = CSharpScriptExecutionState.Create();
            var precedingExecutors = GetPrecedingExecutors(cancellationToken);
            var currentExecutor = GetExecutor(cancellationToken);

            return RunSubmissionsAsync(null, executionState, precedingExecutors, currentExecutor, catchException, cancellationToken);
        }

        /// <summary>
        /// Run the script from the specified state.
        /// </summary>
        /// <param name="previousState">
        /// Previous state of the script execution.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="previousState"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="previousState"/> is not a previous execution state of this script.</exception>
        public new Task<CSharpScriptState<T>> RunFromAsync(CSharpScriptState previousState, CancellationToken cancellationToken)
            => RunFromAsync(previousState, null, cancellationToken);

        /// <summary>
        /// Run the script from the specified state.
        /// </summary>
        /// <param name="previousState">
        /// Previous state of the script execution.
        /// </param>
        /// <param name="catchException">
        /// If specified, any exception thrown by the script top-level code is passed to <paramref name="catchException"/>.
        /// If it returns true the exception is caught and stored on the resulting <see cref="ScriptState"/>, otherwise the exception is propagated to the caller.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="ScriptState"/> that represents the state after running the script, including all declared variables and return value.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="previousState"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="previousState"/> is not a previous execution state of this script.</exception>
        public new Task<CSharpScriptState<T>> RunFromAsync(CSharpScriptState previousState, Func<Exception, bool> catchException = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // The following validation and executor construction may throw;
            // do so synchronously so that the exception is not wrapped in the task.

            if (previousState == null)
            {
                throw new ArgumentNullException(nameof(previousState));
            }

            if (previousState.Script == this)
            {
                // this state is already the output of running this script.
                return Task.FromResult((CSharpScriptState<T>)previousState);
            }

            var precedingExecutors = TryGetPrecedingExecutors(previousState.Script, cancellationToken);
            if (precedingExecutors.IsDefault)
            {
                throw new ArgumentException(ScriptingResources.StartingStateIncompatible, nameof(previousState));
            }

            var currentExecutor = GetExecutor(cancellationToken);
            CSharpScriptExecutionState newExecutionState = previousState.ExecutionState.FreezeAndClone();

            return RunSubmissionsAsync(previousState, newExecutionState, precedingExecutors, currentExecutor, catchException, cancellationToken);
        }

        private async Task<CSharpScriptState<T>> RunSubmissionsAsync(CSharpScriptState previousScriptState,
                                                                               CSharpScriptExecutionState executionState,
                                                                               ImmutableArray<Func<object[], Task>> precedingExecutors,
                                                                               Func<object[], Task> currentExecutor,
                                                                               Func<Exception, bool> catchExceptionOpt,
                                                                               CancellationToken cancellationToken)
        {
            var exceptionOpt = (catchExceptionOpt != null) ? new StrongBox<Exception>() : null;
            var result = await executionState.RunSubmissionsAsync<object>(precedingExecutors, currentExecutor, exceptionOpt, catchExceptionOpt, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return new CSharpScriptState<T>(previousScriptState, executionState, this, (T)result, exceptionOpt?.Value);
        }

        private ImmutableArray<Func<object[], Task>> _lazyPrecedingExecutors;
        private Func<object[], Task<T>> _lazyExecutor;

        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        private Func<object[], Task<T>> GetExecutor(CancellationToken cancellationToken)
        {
            if (_lazyExecutor == null)
            {
                Interlocked.CompareExchange(ref _lazyExecutor, Compiler.CreateExecutor<T>(GetCompilation(), Options, cancellationToken), null);
            }

            return _lazyExecutor;
        }

        /// <exception cref="CompilationErrorException">Compilation has errors.</exception>
        private ImmutableArray<Func<object[], Task>> GetPrecedingExecutors(CancellationToken cancellationToken)
        {
            if (_lazyPrecedingExecutors.IsDefault)
            {
                var preceding = TryGetPrecedingExecutors(null, cancellationToken);
                Debug.Assert(!preceding.IsDefault);
                ImmutableInterlocked.InterlockedCompareExchange(ref _lazyPrecedingExecutors, preceding, default);
            }

            return _lazyPrecedingExecutors;
        }

        internal CSharpScript(ICompiler compiler, IServiceProvider lifetimeScope, string sourceText,
            CSharpScriptOptions options, CSharpScript previousOpt) 
            : base(compiler, lifetimeScope, sourceText, options, previousOpt)
        {
        }
    }

}
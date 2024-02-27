using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.Scripting;

namespace OpenSmc.CSharp.Roslyn
{
    /// <summary>
    /// The result of running a script.
    /// </summary>
    public abstract class CSharpScriptState : IDisposable
    {
        private CSharpScriptState previousState;

        /// <summary>
        /// The script that ran to produce this result.
        /// </summary>
        public CSharpScript Script { get; private set; }

        /// <summary>
        /// Caught exception originating from the script top-level code.
        /// </summary>
        /// <remarks>
        /// Exceptions are only caught and stored here if the API returning the <see cref="ScriptState"/> is instructed to do so. 
        /// By default they are propagated to the caller of the API.
        /// </remarks>
        public Exception Exception { get; private set; }

        internal CSharpScriptExecutionState ExecutionState { get; private set; }

        private ImmutableArray<CSharpScriptVariable> _lazyVariables;
        private ImmutableArray<TypeInfo> _lazyDeclaredNestedTypes;
        private IReadOnlyDictionary<string, int> _lazyVariableMap;

        internal CSharpScriptState(CSharpScriptState previousState, CSharpScriptExecutionState executionState, CSharpScript script, Exception exceptionOpt)
        {
            this.previousState = previousState;
            Debug.Assert(executionState != null);
            Debug.Assert(script != null);

            ExecutionState = executionState;
            Script = script;
            Exception = exceptionOpt;
        }

        /// <summary>
        /// The final value produced by running the script.
        /// </summary>
        public object ReturnValue => GetReturnValue();
        internal abstract object GetReturnValue();

        /// <summary>
        /// Returns variables defined by the scripts in the declaration order.
        /// </summary>
        public ImmutableArray<CSharpScriptVariable> Variables
        {
            get
            {
                if (_lazyVariables == null)
                {
                    ImmutableInterlocked.InterlockedInitialize(ref _lazyVariables, CreateVariables());
                }

                return _lazyVariables;
            }
        }

        /// <summary>
        /// Returns nested types defined by the script.
        /// </summary>
        public ImmutableArray<TypeInfo> DeclaredNestedTypes
        {
            get
            {
                if (_lazyDeclaredNestedTypes == null)
                {
                    ImmutableInterlocked.InterlockedInitialize(ref _lazyDeclaredNestedTypes, CreateDeclaredNestedTypes());
                }

                return _lazyDeclaredNestedTypes;
            }
        }

        /// <summary>
        /// Returns a script variable of the specified name. 
        /// </summary> 
        /// <remarks>
        /// If multiple script variables are defined in the script (in distinct submissions) returns the last one.
        /// Name lookup is case sensitive in C# scripts and case insensitive in VB scripts.
        /// </remarks>
        /// <returns><see cref="ScriptVariable"/> or null, if no variable of the specified <paramref name="name"/> is defined in the script.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public CSharpScriptVariable GetVariable(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            int index;
            return GetVariableMap().TryGetValue(name, out index) ? Variables[index] : null;
        }

        private ImmutableArray<TypeInfo> CreateDeclaredNestedTypes()
        {
            var state = ExecutionState.GetSubmissionState(ExecutionState.SubmissionStateCount - 1);
            
            if (state == null)
                return ImmutableArray<TypeInfo>.Empty;

            return state.GetType().GetTypeInfo().DeclaredNestedTypes.Where(t => !t.Name.StartsWith("<")).ToImmutableArray();
        }

        private ImmutableArray<CSharpScriptVariable> CreateVariables()
        {
            List<CSharpScriptVariable> result;

            var executionState = ExecutionState;
            int startIndex;

            if (previousState == null)
            {
                startIndex = 1;
                result = new List<CSharpScriptVariable>();
            }
            else
            {
                startIndex = previousState.ExecutionState.SubmissionStateCount;
                result = previousState.Variables.ToList();
            }

            // Don't include the globals object (slot #0)
            for (int i = startIndex; i < executionState.SubmissionStateCount; i++)
            {
                var state = executionState.GetSubmissionState(i);
                Debug.Assert(state != null);

                foreach (var field in state.GetType().GetTypeInfo().DeclaredFields)
                {
                    // TODO: synthesized fields of submissions shouldn't be public
                    if (field.IsPublic && field.Name.Length > 0 && (char.IsLetterOrDigit(field.Name[0]) || field.Name[0] == '_'))
                    {
                        var scriptVariable = new CSharpScriptVariable(state, field);
                        result.Add(scriptVariable);
                        OnSetVariable?.Invoke(scriptVariable);
                    }
                }
            }

            var variables = result.ToImmutableArray();

            previousState = null;

            return variables;
        }

        public delegate void SetVariableAsync(CSharpScriptVariable variable);
        public event SetVariableAsync OnSetVariable;

        private IReadOnlyDictionary<string, int> GetVariableMap()
        {
            if (_lazyVariableMap == null)
            {
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Variables.Length; i++)
                {
                    map[Variables[i].Name] = i;
                }

                _lazyVariableMap = map;
            }

            return _lazyVariableMap;
        }

        /// <summary>
        /// Continues script execution from the state represented by this instance by running the specified code snippet.
        /// </summary>
        /// <param name="code">The code to be executed.</param>
        /// <param name="options">Options.</param>
        /// <param name="catchException">
        /// If specified, any exception thrown by the script top-level code is passed to <paramref name="catchException"/>.
        /// If it returns true the exception is caught and stored on the resulting <see cref="ScriptState"/>, otherwise the exception is propagated to the caller.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result of the evaluation</returns>
        public Task<CSharpScriptState<T>> ContinueWithAsync<T>(string code, CSharpScriptOptions options = null, Func<Exception, bool> catchException = null, CancellationToken cancellationToken = default)
            => Script.ContinueWith<T>(code, options).RunFromAsync(this, catchException, cancellationToken);

       

        // How do we resolve overloads? We should use the language semantics.
        // https://github.com/dotnet/roslyn/issues/3720
#if TODO
        /// <summary>
        /// Invoke a method declared by the script.
        /// </summary>
        public object Invoke(string name, params object[] args)
        {
            var func = this.FindMethod(name, args != null ? args.Length : 0);
            if (func != null)
            {
                return func(args);
            }

            return null;
        }

        private Func<object[], object> FindMethod(string name, int argCount)
        {
            for (int i = _executionState.Count - 1; i >= 0; i--)
            {
                var sub = _executionState[i];
                if (sub != null)
                {
                    var type = sub.GetType();
                    var method = FindMethod(type, name, argCount);
                    if (method != null)
                    {
                        return (args) => method.Invoke(sub, args);
                    }
                }
            }

            return null;
        }

        private MethodInfo FindMethod(Type type, string name, int argCount)
        {
            return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>
        /// Create a delegate to a method declared by the script.
        /// </summary>
        public TDelegate CreateDelegate<TDelegate>(string name)
        {
            var delegateInvokeMethod = typeof(TDelegate).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);

            for (int i = _executionState.Count - 1; i >= 0; i--)
            {
                var sub = _executionState[i];
                if (sub != null)
                {
                    var type = sub.GetType();
                    var method = FindMatchingMethod(type, name, delegateInvokeMethod);
                    if (method != null)
                    {
                        return (TDelegate)(object)method.CreateDelegate(typeof(TDelegate), sub);
                    }
                }
            }

            return default(TDelegate);
        }

        private MethodInfo FindMatchingMethod(Type instanceType, string name, MethodInfo delegateInvokeMethod)
        {
            var dprms = delegateInvokeMethod.GetParameters();

            foreach (var mi in instanceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (mi.Name == name)
                {
                    var prms = mi.GetParameters();
                    if (prms.Length == dprms.Length)
                    {
                        // TODO: better matching..
                        return mi;
                    }
                }
            }

            return null;
        }
#endif
        public void Dispose()
        {
            _lazyVariableMap = null;
            Script?.Dispose();
            Script = null;
            Exception = null;
            ExecutionState = null;
        }
    }

    public enum CSharpCompilationType { Script, Interactive }

    public class CSharpScriptOptions
    {
        private CSharpScriptOptions(CSharpScriptOptions copy)
        {
            RoslynScriptOptions = copy.RoslynScriptOptions;
            EnableDebug = copy.EnableDebug;
            CompilationType = copy.CompilationType;
            References = copy.References;
        }
        private CSharpScriptOptions()
        {
        }
        public static readonly CSharpScriptOptions Default = new CSharpScriptOptions();

        public static readonly CSharpScriptOptions Interactive = Default.WithCompilationType(CSharpCompilationType.Interactive);

        public ScriptOptions RoslynScriptOptions { get; private set; } = ScriptOptions.Default;
        public bool EnableDebug { get; private set; }

        public CSharpCompilationType CompilationType { get; private set; }
        public List<MetadataReferenceWrap> References { get; private set; } = new();

        public int Index { get; private set; } = -1;

        public CSharpScriptOptions WithIndex(int index)
        {
            return new CSharpScriptOptions(this)
            {
                Index = index
            };
        }

        public CSharpScriptOptions WithCompilationType(CSharpCompilationType type)
        {
            return new CSharpScriptOptions(this)
            {
                CompilationType = type
            };
        }

        public CSharpScriptOptions WithRoslynScriptOption(ScriptOptions options)
        {
            return new CSharpScriptOptions(this)
            {
                RoslynScriptOptions = options
            };
        }

        public CSharpScriptOptions WithReferences(IEnumerable<MetadataReferenceWrap> references)
        {
            References = new List<MetadataReferenceWrap>(references);
            return this;
        }
    }

    public sealed class CSharpScriptState<T> : CSharpScriptState
    {
        public new T ReturnValue { get; }
        internal override object GetReturnValue() => ReturnValue;

        internal CSharpScriptState(CSharpScriptState previousState, CSharpScriptExecutionState executionState, CSharpScript script, T value, Exception exceptionOpt)
            : base(previousState, executionState, script, exceptionOpt)
        {
            ReturnValue = value;
        }
    }
}


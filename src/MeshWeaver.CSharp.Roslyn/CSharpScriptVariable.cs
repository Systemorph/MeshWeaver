using System.Diagnostics;
using System.Reflection;

namespace MeshWeaver.CSharp.Roslyn
{
    /// <remarks>inspired by Microsoft.CodeAnalysis.Scripting.ScriptVariable</remarks>
    [DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "()}")]
    public sealed class CSharpScriptVariable : IDisposable
    {
        private object instance;
        private readonly FieldInfo field;
        public bool IsDisposed { get; private set; }
        public bool IsExternal { get; set; }

        internal CSharpScriptVariable(object instance, FieldInfo field)
        {
            Debug.Assert(instance != null);
            Debug.Assert(field != null);

            this.instance = instance;
            this.field = field;
        }

        /// <summary>
        /// The name of the variable.
        /// </summary>
        public string Name => field.Name;

        /// <summary>
        /// The type of the variable.
        /// </summary>
        public Type Type => field.FieldType;

        /// <summary>
        /// True if the variable can't be written to (it's declared as readonly or a constant).
        /// </summary>
        public bool IsReadOnly => field.IsInitOnly || field.IsLiteral;

        /// <summary>
        /// The value of the variable after running the script.
        /// </summary>
        /// <exception cref="InvalidOperationException">Variable is read-only or a constant.</exception>
        /// <exception cref="ArgumentException">The type of the specified <paramref name="value"/> isn't assignable to the type of the variable.</exception>
        public object Value
        {
            get
            {
                if (IsDisposed)
                    throw new InvalidOperationException(ScriptingResources.CannotGetDisposedVariable);

                return field.GetValue(instance);
            }

            set
            {
                if (field.IsInitOnly)
                    throw new InvalidOperationException(ScriptingResources.CannotSetReadOnlyVariable);

                ForceSetValue(value);
            }
        }

        public void ForceSetValue(object value)
        {
            if (field.IsLiteral)
                throw new InvalidOperationException(ScriptingResources.CannotSetConstantVariable);

            field.SetValue(instance, value);
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            if (!IsReadOnly)
                field.SetValue(instance, GetDefaultValue(field.FieldType));

            instance = null;

            IsDisposed = true;
        }

        /// <remarks>taken from https://stackoverflow.com/a/2686723/12213117</remarks>
        private static object GetDefaultValue(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        private string GetDebuggerDisplay() => $"{Name}: {(IsDisposed ? "<Disposed>" : Value ?? "<null>")}";
    }
}
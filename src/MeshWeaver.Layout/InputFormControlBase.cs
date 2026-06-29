namespace MeshWeaver.Layout
{
    /// <summary>
    /// Abstract base record for text-input form controls (text fields, number fields, etc.)
    /// that expose common constraints such as MaxLength, MinLength, and Size.
    /// </summary>
    /// <typeparam name="TControl">The concrete control type, used for fluent builder returns.</typeparam>
    /// <param name="Data">The data binding for the control's value.</param>
    public abstract record InputFormControlBase<TControl>(object Data) : FormControlBase<TControl>(Data), IInputFormControl
        where TControl : InputFormControlBase<TControl>
    {

        /// <summary>
        /// Gets or initializes the maximum length for the number field control.
        /// </summary>
        public object? MaxLength { get; init; }

        /// <summary>
        /// Gets or initializes the minimum length for the number field control.
        /// </summary>
        public object? MinLength { get; init; }

        /// <summary>
        /// Gets or initializes the size of the number field control.
        /// </summary>
        public object? Size { get; init; } = 30;

    }
}

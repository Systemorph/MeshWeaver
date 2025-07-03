namespace MeshWeaver.Layout
{
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

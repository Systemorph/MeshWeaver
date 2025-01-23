namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a form component with customizable properties.
    /// </summary>
    public interface IFormControl : IUiControl
    {
        /// <summary>
        /// Gets or initializes the data associated with the form component.
        /// </summary>
        object Data { get; init; }
        /// <summary>
        /// Label of the form component
        /// </summary>
        object Label { get; init; }

        IFormControl WithLabel(object label);
    }
}

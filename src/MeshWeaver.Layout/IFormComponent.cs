namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents a form component with customizable properties.
    /// </summary>
    public interface IFormComponent : IUiControl
    {
        /// <summary>
        /// Gets or initializes the data associated with the form component.
        /// </summary>
        object Data { get; init; }
    }
}

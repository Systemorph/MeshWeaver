using MeshWeaver.Data;

namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents an event that occurs when an area is clicked.
    /// </summary>
    /// <param name="Area">The area that was clicked.</param>
    public record ClickedEvent(string Area) : WorkspaceMessage
    {
        /// <summary>
        /// Gets or initializes the payload associated with the clicked event.
        /// </summary>
        public object Payload { get; init; }
    }
}

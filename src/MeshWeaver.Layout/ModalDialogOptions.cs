namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents the options for a modal dialog.
    /// </summary>
    /// <param name="Size">The size of the modal dialog.</param>
    /// <param name="IsClosable">Indicates whether the modal dialog is closable.</param>
    public record ModalDialogOptions(string Size, bool IsClosable)
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ModalDialogOptions"/> class with default values.
        /// </summary>
        public ModalDialogOptions()
            : this(Sizes.Medium, false)
        {
        }

        /// <summary>
        /// Provides constants for different modal dialog sizes.
        /// </summary>
        public static class Sizes
        {
            /// <summary>
            /// Represents a medium-sized modal dialog.
            /// </summary>
            public const string Medium = "M";

            /// <summary>
            /// Represents a large-sized modal dialog.
            /// </summary>
            public const string Large = "L";

            /// <summary>
            /// Represents a small-sized modal dialog.
            /// </summary>
            public const string Small = "S";
        }
    }
}
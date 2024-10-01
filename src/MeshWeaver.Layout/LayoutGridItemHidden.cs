namespace MeshWeaver.Layout
{
    /// <summary>
    /// Specifies the visibility options for a layout grid item based on screen size.
    /// </summary>
    public enum LayoutGridItemHidden
    {
        /// <summary>
        /// The item is always visible.
        /// </summary>
        None,

        /// <summary>
        /// The item is hidden on extra small screens.
        /// </summary>
        Xs,

        /// <summary>
        /// The item is hidden on extra small screens and smaller.
        /// </summary>
        XsAndDown,

        /// <summary>
        /// The item is hidden on small screens.
        /// </summary>
        Sm,

        /// <summary>
        /// The item is hidden on small screens and smaller.
        /// </summary>
        SmAndDown,

        /// <summary>
        /// The item is hidden on medium screens.
        /// </summary>
        Md,

        /// <summary>
        /// The item is hidden on medium screens and smaller.
        /// </summary>
        MdAndDown,

        /// <summary>
        /// The item is hidden on large screens.
        /// </summary>
        Lg,

        /// <summary>
        /// The item is hidden on large screens and smaller.
        /// </summary>
        LgAndDown,

        /// <summary>
        /// The item is hidden on extra large screens.
        /// </summary>
        Xl,

        /// <summary>
        /// The item is hidden on extra large screens and smaller.
        /// </summary>
        XlAndDown,

        /// <summary>
        /// The item is hidden on extra extra large screens.
        /// </summary>
        Xxl,

        /// <summary>
        /// The item is hidden on extra extra large screens and larger.
        /// </summary>
        XxlAndUp,

        /// <summary>
        /// The item is hidden on extra large screens and larger.
        /// </summary>
        XlAndUp,

        /// <summary>
        /// The item is hidden on large screens and larger.
        /// </summary>
        LgAndUp,

        /// <summary>
        /// The item is hidden on medium screens and larger.
        /// </summary>
        MdAndUp,

        /// <summary>
        /// The item is hidden on small screens and larger.
        /// </summary>
        SmAndUp,

        /// <summary>
        /// The item is hidden on extra extra large screens and smaller.
        /// </summary>
        XxlAndDown,
    }
}

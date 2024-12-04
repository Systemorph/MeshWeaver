using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Bubble
{
    public record BubbleDataSet : DataSet, IDataSetWithOrder, IDataSetWithPointStyle
    {
        #region General
        // https://www.chartjs.org/docs/latest/charts/bubble.html#general
        /// <summary>
        /// Draw the active points of a dataset over the other points of the dataset
        /// </summary>
        public bool? DrawActiveElementsOnTop { get; init; }

        /// <summary>
        /// The drawing order of dataset. Also affects order for stacking, tooltip and legend.
        /// </summary>
        public int? Order { get; init; }
        #endregion General

        #region Styling
        // https://www.chartjs.org/docs/latest/charts/bubble.html#styling
        /// <summary>
        /// Bubble shape style.
        /// </summary>
        public Shapes? PointStyle { get; init; }

        /// <summary>
        /// Bubble rotation (in degrees).
        /// </summary>
        public int? Rotation { get; init; }

        /// <summary>
        /// Bubble radius (in pixels).
        /// </summary>
        public int? Radius { get; init; }
        #endregion Styling

        #region Interactions
        // https://www.chartjs.org/docs/latest/charts/bubble.html#interactions
        /// <summary>
        /// Bubble additional radius for hit detection (in pixels).
        /// </summary>
        public int? HitRadius { get; init; }

        /// <summary>
        /// Bubble border width when hovered (in pixels).
        /// </summary>
        public int? HoverBorderWidth { get; init; }

        /// <summary>
        /// Bubble additional radius when hovered (in pixels).
        /// </summary>
        public int? HoverRadius { get; init; }
        #endregion Interactions
    }
}

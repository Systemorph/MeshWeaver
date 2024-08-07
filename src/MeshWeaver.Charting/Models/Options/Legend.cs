
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options
{
    // https://www.chartjs.org/docs/3.5.1/configuration/legend.html
    public record Legend
    {
        /// <summary>
        /// Is the legend displayed.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Position of the legend. Possible values are 'top', 'left', 'bottom' and 'right'.
        /// </summary>
        public Positions? Position { get; init; }

        /// <summary>
        /// Alignment of the legend.
        /// </summary>
        public string Align { get; init; }

        /// <summary>
        /// Maximum height of the legend, in pixels.
        /// </summary>
        public int? MaxHeight { get; init; }

        /// <summary>
        /// Maximum width of the legend, in pixels.
        /// </summary>
        public int? MaxWidth { get; init; }

        /// <summary>
        /// Marks that this box should take the full width/height of the canvas (moving other boxes). This is unlikely to need to be changed in day-to-day use.
        /// </summary>
        public bool? FullSize { get; init; }

        /// <summary>
        /// A callback that is called when a 'click' event is registered on top of a label item.
        /// </summary>
        public object OnClick { get; init; }

        /// <summary>
        /// A callback that is called when a 'mousemove' event is registered on top of a label item.
        /// </summary>
        public object OnHover { get; init; }

        /// <summary>
        /// A callback that is called when a 'mousemove' event is registered outside of a previously hovered label item. Arguments: [event, legendItem, legend].
        /// </summary>
        public object OnLeave { get; init; }

        /// <summary>
        /// Legend will show datasets in reverse order
        /// </summary>
        public bool? Reverse { get; init; }

        public LegendLabel Labels { get; init; }

        /// <summary>
        /// true for rendering the legends from right to left.
        /// </summary>
        public bool? Rtl { get; init; }

        /// <summary>
        /// This will force the text direction 'rtl' or 'ltr' on the canvas for rendering the legend, regardless of the css specified on the canvas
        /// </summary>
        public string TextDirection { get; init; }

        public Title Title { get; init; }

        public Legend AtPosition(Positions pos) => this with { Position = pos };

        public Legend WithAlign(string alignment) => this with { Align = alignment };

        public Legend Reversed() => this with { Reverse = true };

        public Legend WithDisplay(bool display) => this with { Display = display };
    }
}

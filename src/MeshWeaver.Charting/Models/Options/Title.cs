using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options
{
    // https://www.chartjs.org/docs/3.5.1/configuration/title.html
    public record Title
    {
        /// <summary>
        /// Alignment of the title.
        /// </summary>
        public string Align { get; init; }

        /// <summary>
        /// Color of text.
        /// </summary>
        public ChartColor Color { get; init; }

        /// <summary>
        /// Is the legend title displayed.
        /// </summary>
        public bool? Display { get; init; }

        /// <summary>
        /// Marks that this box should take the full width/height of the canvas. If false, the box is sized and placed above/beside the chart area.
        /// </summary>
        public bool? FullSize { get; init; }

        /// <summary>
        /// Position of title.
        /// </summary>
        public Positions? Position { get; init; }

        /// <summary>
        /// Font of the title.
        /// </summary>
        public Font Font { get; init; }

        /// <summary>
        /// Number of pixels to add above and below the title text.
        /// </summary>
        public int? Padding { get; init; }

        /// <summary>
        /// Title text.
        /// string or string[]
        /// </summary>
        public object Text { get; init; }

        public Title AtPosition(Positions position) => this with { Position = position };

        public Title WithFontSize(int i) => this with { Font = (Font ?? new Font()) with { Size = i } };

        public Title WithFontColor(string color) => this with { Color = ChartColor.FromHexString(color) };

        public Title WithFontStyle(string fontStyle) => this with { Font = Font with { Style = fontStyle } };

        public Title WithFontFamily(string fontFamily) => this with { Font = Font with { Family = fontFamily } };

        public Title WithPadding(int pixels) => this with { Padding = pixels };

        public Title WithLineHeight(double height) => this with { Font = Font with { LineHeight = height } };

        public Title AlignAtStart() => this with { Align = "start" };

    }
}

namespace MeshWeaver.Application.Styles;

public static class Colors
{
    public static class Palettes
    {
        /// <summary>
        /// in notebooks we ll have ability to do following to change palette
        /// MeshWeaver.Application.Styles.Colors.Default = new ("color1","color2","color3",...)
        /// we support hex codes and color names
        /// </summary>
        public static ColorPalette Default { get; set; } = new("#e6194B", "#3cb44b", "#ffe119", "#4363d8", "#f58231",
                                                               "#911eb4", "#42d4f4", "#f032e6", "#bfef45", "#fabed4",
                                                               "#469990", "#dcbeff", "#9A6324", "#fffac8", "#800000",
                                                               "#aaffc3", "#808000", "#ffd8b1", "#000075", "#a9a9a9",
                                                               "#ffffff", "#000000");
    }


    public const string Button = "#0171FF";
    public const string Submission = "#A25BDE";
    public const string Reopen = "#5BC0DE";
    public const string Review = "#03CB5D";
    public const string SignOff = "#0171FF";
    public const string Import = "#f7d10f";
    public const string Failed = "#d42708";
    public const string Warning = "#f2e03f";
    public const string Info = "#a0d4e8";
    public const string Success = "#069909";
    public const string Cancel = "#EDEDED";
    public const string Text = "#292b36";
}


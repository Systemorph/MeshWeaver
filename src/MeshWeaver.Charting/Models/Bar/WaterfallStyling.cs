using System.Globalization;

namespace MeshWeaver.Charting.Models.Bar;

public record WaterfallStyling
{

    public WaterfallStyling()
    {
        FormatFuncDecrement = d => NumberFormat(d);
        FormatFuncIncrement = d => NumberFormat(d);
        FormatFuncTotal = d => NumberFormat(d);
    }
    /// <summary>
    /// Gets the color for increment bars.
    /// </summary>
    public string IncrementColor { get; init; } = "#66B7FF";

    /// <summary>
    /// Gets the color for total bars.
    /// </summary>
    public string TotalColor { get; init; } = "#A6ABC2";

    /// <summary>
    /// Gets the color for decrement bars.
    /// </summary>
    public string DecrementColor { get; init; } = "#F56295";

    /// <summary>
    /// Gets the font color for labels.
    /// </summary>
    public string LabelsFontColor { get; init; } = "white";

    /// <summary>
    /// Gets the font size for labels.
    /// </summary>
    public int LabelsFontSize { get; init; } = 14;

    /// <summary>
    /// Gets the function to format increment values.
    /// </summary>
    public Func<double, string> FormatFuncIncrement { get; init; } 

    /// <summary>
    /// Gets the function to format decrement values.
    /// </summary>
    public Func<double, string> FormatFuncDecrement { get; init; }

    /// <summary>
    /// Gets the function to format total values.
    /// </summary>
    public Func<double, string> FormatFuncTotal { get; init; }


    public Func<double, string> NumberFormat { get; init; } = d => d.ToString("#.##");

    public WaterfallStyling WithIncrementColor(string color)
    => this with { IncrementColor = color };

    private string ThousandsFormat(double value)
    {
        //if (Math.Abs(value) >= 100000000)
        //    return ThousandsFormat(value / 1000000) + " M";

        //if (Math.Abs(value) >= 100000)
        //    return ThousandsFormat(value / 1000) + " K";

        if (Math.Abs(value) >= 10000)
            return (value / 1000).ToString("0.#") + " K";

        return value.ToString("#,0");
    }
    //https://gist.github.com/FiercestT/8201fc1a57ea28d5b09f8cd5892d12ab
    private enum Suffixes
    {
        p, // p is a placeholder if the value is under 1 thousand
        K, // Thousand
        M, // Million
        B, // Billion
        T, // Trillion
        Q, //Quadrillion
    }

    //Formats numbers in Millions, Billions, etc.
    private string Format(long value)
    {
        int decimals = 0; //How many decimals to round to
        string r = value.ToString(); //Get a default return value

        foreach (Suffixes suffix in Enum.GetValues(typeof(Suffixes))) //For each value in the suffixes enum
        {
            var currentVal = 1 * Math.Pow(10, (int)suffix * 3); //Assign the amount of digits to the base 10
            var suff = Enum.GetName(typeof(Suffixes), (int)suffix); //Get the suffix value
            if ((int)suffix == 0) //If the suffix is the p placeholder
                suff = string.Empty; //set it to an empty string

            if (value >= currentVal)
                r = Math.Round((value / currentVal), decimals, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture) + suff; //Set the return value to a rounded value with suffix
            else
                return r; //If the value wont go anymore then return
        }
        return r; // Default Return
    }

    public WaterfallStyling Thousands()
        => this with { NumberFormat = value => (value < 0 ? "-" : "") + Format(Math.Abs((long)value)) };

    public WaterfallStyling Round()
        => this with { NumberFormat = value => Math.Round(value).ToString(CultureInfo.InvariantCulture) };

    public WaterfallStyling WithDecrementColor(string color)
        => this with { DecrementColor = color };

    public WaterfallStyling WithTotalColor(string color) => 
        this with { TotalColor = color };

    public WaterfallStyling WithLabelsFontColor(string color) =>
        this with { LabelsFontColor = color };

    public WaterfallStyling WithLabelsFontSize(int size) =>
        this with { LabelsFontSize = size };

}

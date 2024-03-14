using System.Globalization;
using OpenSmc.Charting.Models.Bar;
using static System.String;

namespace OpenSmc.Charting.Builders.OptionsBuilders;

public record WaterfallStylingBuilder
{
    public WaterfallStylingBuilder()
    {
        Styling = new("#66B7FF", "#A6ABC2", "#F56295",
                      "white", 14,
                      d => numberFormat(d),
                      d => numberFormat(d),
                      d => numberFormat(d));
    }
    private WaterfallStyling Styling { get; set; }

    private Func<double, string> numberFormat = d => d.ToString("#.##");

    public WaterfallStyling Build() => Styling;

    public WaterfallStylingBuilder IncrementColor(string color)
    {
        Styling = Styling with { IncrementColor = color };
        return this;
    }

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
                suff = Empty; //set it to an empty string

            if (value >= currentVal)
                r = Math.Round((value / currentVal), decimals, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture) + suff; //Set the return value to a rounded value with suffix
            else
                return r; //If the value wont go anymore then return
        }
        return r; // Default Return
    }

    public WaterfallStylingBuilder Thousands()
    {
        numberFormat = value => (value < 0 ? "-" : "") + Format(Math.Abs((long)value));
        return this;
    }

    public WaterfallStylingBuilder Round()
    {
        numberFormat = value => Math.Round(value).ToString(CultureInfo.InvariantCulture);
        return this;
    }

    public WaterfallStylingBuilder DecrementColor(string color)
    {
        Styling = Styling with { DecrementColor = color };
        return this;
    }

    public WaterfallStylingBuilder TotalColor(string color)
    {
        Styling = Styling with { TotalColor = color };
        return this;
    }

    public WaterfallStylingBuilder LabelsFontColor(string color)
    {
        Styling = Styling with { LabelsFontColor = color };
        return this;
    }

    public WaterfallStylingBuilder LabelsFontSize(int size)
    {
        Styling = Styling with { LabelsFontSize = size };
        return this;
    }
}
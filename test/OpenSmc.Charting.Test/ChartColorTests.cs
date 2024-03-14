using System.Globalization;
using FluentAssertions;
using OpenSmc.Charting.Helpers;

namespace OpenSmc.Charting.Test;

public class ChartColorTests
{
    [Fact]
    public void Too_Big_Alpha_Throws_ArgumentOutOfRangeException()
    {
        const byte red = 0;
        const byte green = 0;
        const byte blue = 0;
        const double alpha = 1.1;

        Assert.Throws<ArgumentOutOfRangeException>(() => ChartColor.FromRgba(red, green, blue, alpha));
    }

    [Fact]
    public void Too_Small_Alpha_Throws_ArgumentOutOfRangeException()
    {
        const byte red = 0;
        const byte green = 0;
        const byte blue = 0;
        const double alpha = -5;

        Assert.Throws<ArgumentOutOfRangeException>(() => ChartColor.FromRgba(red, green, blue, alpha));
    }

    [Fact]
    public void FromRgba_Populates_Correct_Values()
    {
        const byte expectedRed = 187;
        const byte expectedGreen = 55;
        const byte expectedBlue = 4;
        const double expectedAlpha = 0.65;

        var actualColor = ChartColor.FromRgba(expectedRed, expectedGreen, expectedBlue, expectedAlpha);

        actualColor.Red.Should().Be(expectedRed);
        actualColor.Green.Should().Be(expectedGreen);
        actualColor.Blue.Should().Be(expectedBlue);
        actualColor.Alpha.Should().Be(expectedAlpha);
    }

    [Fact]
    public void FromRgb_Populates_Correct_Values()
    {
        const byte expectedRed = 66;
        const byte expectedGreen = 99;
        const byte expectedBlue = 111;

        var actualColor = ChartColor.FromRgb(expectedRed, expectedGreen, expectedBlue);


        actualColor.Red.Should().Be(expectedRed);
        actualColor.Green.Should().Be(expectedGreen);
        actualColor.Blue.Should().Be(expectedBlue);
    }

    [Fact]
    public void FromRgb_Populates_Alpha_With_One()
    {
        const double expectedAlpha = 1.0;

        var actualColor = ChartColor.FromRgb(0, 0, 0);

        actualColor.Alpha.Should().Be(expectedAlpha);
    }

    [Fact]
    public void FromHexString_Throws_FormatException_Without_Hex_Specifier()
    {
        const string hexString = "12abef";

        Assert.Throws<FormatException>(() => ChartColor.FromHexString(hexString));
    }


    [Fact]
    public void FromHexString_Throws_FormatException_With_False_Length()
    {
        const string tooShortHexString = "#12abe";
        const string tooLongHexString = "#12abefa";

        Assert.Throws<FormatException>(() => ChartColor.FromHexString(tooShortHexString));
        Assert.Throws<FormatException>(() => ChartColor.FromHexString(tooLongHexString));
    }

    [Fact]
    public void FromHexString_Throws_FormatException_Invalid_Hex_Character()
    {
        const string invalidHexString = "#12abex";

        Assert.Throws<FormatException>(() => ChartColor.FromHexString(invalidHexString));
    }

    [Fact]
    public void FromHexString_Populates_Correct_Values()
    {
        const byte expectedRed = 154;
        const byte expectedGreen = 89;
        const byte expectedBlue = 77;
        const double expectedAlpha = 1.0;

        var actualColor = ChartColor.FromHexString($"#{expectedRed:X}{expectedGreen:X}{expectedBlue:X}");


        actualColor.Red.Should().Be(expectedRed);
        actualColor.Green.Should().Be(expectedGreen);
        actualColor.Blue.Should().Be(expectedBlue);
        actualColor.Alpha.Should().Be(expectedAlpha);
    }

    [Fact]
    public void GetRandomChartColor_With_Random_Alpha_Returns_Values_In_Boundary()
    {
        var randomColor = ChartColor.CreateRandomChartColor(true);

        randomColor.Red.Should().BeInRange(0, 255);
        randomColor.Green.Should().BeInRange(0, 255);
        randomColor.Blue.Should().BeInRange(0, 255);
        randomColor.Alpha.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void GetRandomChartColor_Without_Random_Alpha_Returns_Values_In_Boundary_And_Alpha_One()
    {
        const double expectedAlpha = 1.0;

        var randomColor = ChartColor.CreateRandomChartColor(false);

        randomColor.Red.Should().BeInRange(0, 255);
        randomColor.Green.Should().BeInRange(0, 255);
        randomColor.Blue.Should().BeInRange(0, 255);
        randomColor.Alpha.Should().Be(expectedAlpha);
    }

    [Fact]
    public void ToString_Returns_Correct_Rgba_String_Representation()
    {
        const byte red = 4;
        const byte green = 65;
        const byte blue = 154;
        const double alpha = 0.232;

        var expectedString = $"rgba({red}, {green}, {blue}, {alpha.ToString(CultureInfo.InvariantCulture)})";

        var color = ChartColor.FromRgba(red, green, blue, alpha);
        var actualString = color.ToString();

        actualString.Should().Be(expectedString);
    }

    [Fact]
    public void ToString_Comma_Culture_Returns_Correct_Rgba_String_Representation()
    {
        const byte red = 4;
        const byte green = 65;
        const byte blue = 154;
        const double alpha = 0.232;

        CultureInfo.CurrentCulture = CultureInfo.CreateSpecificCulture("nl-NL");
        var expectedString = $"rgba({red}, {green}, {blue}, {alpha.ToString(CultureInfo.InvariantCulture)})";

        var color = ChartColor.FromRgba(red, green, blue, alpha);
        var actualString = color.ToString();

        actualString.Should().Be(expectedString);
    }
}
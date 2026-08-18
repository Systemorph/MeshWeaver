using System;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;
using Icon = MeshWeaver.Domain.Icon;

namespace MeshWeaver.Layout.Test;

public class LayoutClientExtensionsTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration config)
    {
        return base.ConfigureHost(config);
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_ActualBehavior()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = 3.14;

        // Act - Let's see what actually happens
        var result = hub.ConvertSingle<int>(doubleValue, null);

        // Assert - Document the current behavior - Convert.ChangeType truncates doubles to int
        result.Should().Be(3); // This is what Convert.ChangeType does - truncation
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_LargeValue_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = double.MaxValue; // This is too large for int

        // Act & Assert - This should throw
        Action act = () => hub.ConvertSingle<int>(doubleValue, null);
        
        act.Should().Throw<OverflowException>("Large double values should overflow when converting to int");
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_WithTruncation_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = 3.14;

        // Act - This should work after we fix the implementation
        var result = hub.ConvertSingle<int>(doubleValue, null);

        // Assert
        result.Should().Be(3); // Truncated to integer
    }

    [Fact]
    public void ConvertSingle_DoubleToNullableInt_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = 3.14;

        // Act - This should work after we fix the implementation
        var result = hub.ConvertSingle<int?>(doubleValue, null);
        
        result.Should().Be(3); // Truncated to integer
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_InfinityValue_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = double.PositiveInfinity;

        // Act & Assert - This should throw
        Action act = () => hub.ConvertSingle<int>(doubleValue, null);
        
        act.Should().Throw<OverflowException>("Infinity values should overflow when converting to int");
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_NaNValue_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = double.NaN;

        // Act & Assert - This should throw
        Action act = () => hub.ConvertSingle<int>(doubleValue, null);
        
        act.Should().Throw<OverflowException>("NaN values should overflow when converting to int");
    }

    [Fact]
    public void ConvertSingle_DoubleToInt_ExactValue_ShouldWork() 
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = 42.0;

        // Act
        var result = hub.ConvertSingle<int>(doubleValue, null);

        // Assert
        result.Should().Be(42);
    }

    [Fact]
    public void ConvertSingle_FloatToInt_ShouldWork()
    {
        // Arrange  
        var hub = GetHost();
        float floatValue = 25.7f;

        // Act
        var result = hub.ConvertSingle<int>(floatValue, null);

        // Assert
        result.Should().Be(25); // Truncated
    }

    [Fact]
    public void ConvertSingle_IntToDouble_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        int intValue = 42;

        // Act 
        var result = hub.ConvertSingle<double>(intValue, null);

        // Assert
        result.Should().Be(42.0);
    }

    [Fact]
    public void ConvertSingle_StringToInt_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        string stringValue = "123";

        // Act
        var result = hub.ConvertSingle<int>(stringValue, null);

        // Assert
        result.Should().Be(123);
    }

    [Fact]
    public void ConvertSingle_NullValue_ShouldReturnDefault()
    {
        // Arrange
        var hub = GetHost();

        // Act
        var result = hub.ConvertSingle<int>(null, null);

        // Assert
        result.Should().Be(default(int)); // Should be 0
    }

    [Fact]
    public void ConvertSingle_NullValue_WithDefaultValue_ShouldReturnDefaultValue()
    {
        // Arrange
        var hub = GetHost();
        int defaultValue = 99;

        // Act
        var result = hub.ConvertSingle<int>(null, null, defaultValue);

        // Assert
        result.Should().Be(99);
    }

    // Nullable to non-nullable tests
    [Fact]
    public void ConvertSingle_NullableDoubleToInt_WithValue_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = 3.14;

        // Act
        var result = hub.ConvertSingle<int>(nullableDouble, null);

        // Assert
        result.Should().Be(3);
    }

    [Fact]
    public void ConvertSingle_NullableDoubleToInt_WithNullValue_ShouldReturnDefault()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = null;

        // Act
        var result = hub.ConvertSingle<int>(nullableDouble, null, 42);

        // Assert
        result.Should().Be(42);
    }

    [Fact]
    public void ConvertSingle_NullableIntToDouble_WithValue_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        int? nullableInt = 25;

        // Act
        var result = hub.ConvertSingle<double>(nullableInt, null);

        // Assert
        result.Should().Be(25.0);
    }

    [Fact]
    public void ConvertSingle_NullableIntToDouble_WithNullValue_ShouldReturnDefault()
    {
        // Arrange
        var hub = GetHost();
        int? nullableInt = null;

        // Act
        var result = hub.ConvertSingle<double>(nullableInt, null, 3.14);

        // Assert
        result.Should().Be(3.14);
    }

    // Non-nullable to nullable tests
    [Fact]
    public void ConvertSingle_DoubleToNullableInt_WithValue_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = 7.89;

        // Act
        var result = hub.ConvertSingle<int?>(doubleValue, null);

        // Assert
        result.Should().Be(7);
    }

    [Fact]
    public void ConvertSingle_IntToNullableDouble_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        int intValue = 42;

        // Act
        var result = hub.ConvertSingle<double?>(intValue, null);

        // Assert
        result.Should().Be(42.0);
    }

    [Fact]
    public void ConvertSingle_LargeDoubleToNullableInt_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        double doubleValue = double.MaxValue;

        // Act & Assert
        Action act = () => hub.ConvertSingle<int?>(doubleValue, null);
        
        act.Should().Throw<OverflowException>("Large double values should overflow when converting to nullable int");
    }

    // Nullable to nullable tests
    [Fact]
    public void ConvertSingle_NullableDoubleToNullableInt_WithValue_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = 12.34;

        // Act
        var result = hub.ConvertSingle<int?>(nullableDouble, null);

        // Assert
        result.Should().Be(12);
    }

    [Fact]
    public void ConvertSingle_NullableDoubleToNullableInt_WithNullValue_ShouldReturnDefault()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = null;

        // Act
        var result = hub.ConvertSingle<int?>(nullableDouble, null, 99);

        // Assert
        result.Should().Be(99);
    }

    [Fact]
    public void ConvertSingle_NullableDoubleToNullableInt_WithNullValue_NoDefault_ShouldReturnNull()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = null;

        // Act
        var result = hub.ConvertSingle<int?>(nullableDouble, null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ConvertSingle_NullableIntToNullableDouble_WithValue_ShouldWork()
    {
        // Arrange
        var hub = GetHost();
        int? nullableInt = 15;

        // Act
        var result = hub.ConvertSingle<double?>(nullableInt, null);

        // Assert
        result.Should().Be(15.0);
    }

    [Fact]
    public void ConvertSingle_NullableFloatToNullableInt_WithLargeValue_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        float? nullableFloat = float.MaxValue;

        // Act & Assert
        Action act = () => hub.ConvertSingle<int?>(nullableFloat, null);
        
        act.Should().Throw<OverflowException>("Large float values should overflow when converting to nullable int");
    }

    [Fact]
    public void ConvertSingle_NullableDoubleNaN_ToNullableInt_ShouldThrow()
    {
        // Arrange
        var hub = GetHost();
        double? nullableDouble = double.NaN;

        // Act & Assert
        Action act = () => hub.ConvertSingle<int?>(nullableDouble, null);

        act.Should().Throw<OverflowException>("NaN values should throw when converting to nullable int");
    }

    // ---- Issue #322: a NUMBER/BOOL JSON token bound into a string-typed (read-only) LabelControl -------
    // The read-only Overview binds a numeric/boolean scalar into a string Label. Before the fix,
    // ConvertJson<string> ran Deserialize<string>("322.844") which throws JsonException on a number
    // token → the catch returned null → the field rendered BLANK until click-to-edit. It must now
    // render the value's text, the way a JSON array/object slot already does.

    [Fact]
    public void ConvertSingle_NumberJsonElement_ToString_RendersDecimalText()
    {
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement(322.844m);

        var result = hub.ConvertSingle<string>(element, null);

        result.Should().Be("322.844", "a JSON number bound into a string Label must render as text, not blank");
    }

    [Fact]
    public void ConvertSingle_IntegerJsonElement_ToString_RendersIntegerText()
    {
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement(6);

        var result = hub.ConvertSingle<string>(element, null);

        result.Should().Be("6");
    }

    [Fact]
    public void ConvertSingle_TrueJsonElement_ToString_RendersTrue()
    {
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement(true);

        var result = hub.ConvertSingle<string>(element, null);

        result.Should().Be("true", "a JSON boolean bound into a string Label must render its value, not blank");
    }

    [Fact]
    public void ConvertSingle_FalseJsonElement_ToString_RendersFalse()
    {
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement(false);

        var result = hub.ConvertSingle<string>(element, null);

        result.Should().Be("false");
    }

    [Fact]
    public void ConvertSingle_StringJsonElement_ToString_StillWorks()
    {
        // Regression: a genuine JSON string token must keep deserializing cleanly to string.
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement("hello");

        var result = hub.ConvertSingle<string>(element, null);

        result.Should().Be("hello");
    }

    [Fact]
    public void ConvertSingle_NumberJsonElement_ToDouble_StillDeserializes()
    {
        // The numeric edit control binds the CLR type; that path must be untouched by the string fix.
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement(322.844m);

        var result = hub.ConvertSingle<double>(element, null);

        result.Should().Be(322.844);
    }

    // ---- NavLink icon binding: MeshNode.Icon is a raw STRING (Fluent name / URL / inline SVG) ------
    // A layout area (e.g. MarkdownOverviewLayoutArea's sub-node nav) feeds MeshNodeImageHelper
    // .ResolveNodeIcon(child) — a string — into NavLinkControl.Icon, which the Blazor NavItemView
    // binds into a MeshWeaver.Domain.Icon-typed slot. Before the fix, ConvertString<Icon> threw
    // InvalidOperationException ("Cannot convert /static/NodeTypeIcons/document.svg to
    // MeshWeaver.Domain.Icon") on EVERY render — the memex-cloud prod error storm. The conversion
    // must be TOTAL: all legitimate forms parse, garbage degrades, nothing throws.

    private const string InlineSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M0 0h24v24H0z\"/></svg>";

    [Fact]
    public void ConvertSingle_IconUrlString_ParsesToUrlIcon()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>("/static/NodeTypeIcons/document.svg", null);

        result.Should().NotBeNull("a URL-form node icon must bind, not throw (prod error storm)");
        result!.Provider.Should().Be(Icon.UrlProvider);
        result.Id.Should().Be("/static/NodeTypeIcons/document.svg");
    }

    [Fact]
    public void ConvertSingle_InlineSvgString_ParsesToInlineSvgIcon()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>(InlineSvg, null);

        result.Should().NotBeNull();
        result!.Provider.Should().Be(Icon.InlineSvgProvider);
        result.Id.Should().Be(InlineSvg, "the markup renders verbatim client-side");
        result.Size.Should().Be(MeshWeaver.Domain.IconSize.Custom);
    }

    [Fact]
    public void ConvertSingle_FluentIconNameString_ParsesToFluentIcon()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>("Document", null);

        result.Should().NotBeNull();
        result!.Provider.Should().Be(Icon.FluentProvider);
        result.Id.Should().Be("Document");
    }

    [Fact]
    public void ConvertSingle_EmojiIconString_DegradesToTextGlyph()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>("\U0001F680", null);

        result.Should().NotBeNull();
        result!.Provider.Should().Be(Icon.TextProvider);
        result.Id.Should().Be("\U0001F680");
    }

    [Fact]
    public void ConvertSingle_GarbageIconString_NeverThrows()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>("??!! not-an-icon", null);

        result.Should().NotBeNull("an unknown form must degrade gracefully, never throw");
        result!.Provider.Should().Be(Icon.TextProvider);
    }

    [Fact]
    public void ConvertSingle_WhitespaceIconString_ReturnsNull()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<Icon>("  ", null);

        result.Should().BeNull();
    }

    [Fact]
    public void ConvertSingle_IconStringJsonElement_ParsesSameAsString()
    {
        // The same raw string arriving as a JSON string token (the data-bound pointer path) went
        // through Deserialize<Icon>, threw JsonException, and logged an error per render — the
        // JsonElement shape must route through the same total parse.
        var hub = GetHost();
        var element = JsonSerializer.SerializeToElement("/static/NodeTypeIcons/document.svg");

        var result = hub.ConvertSingle<Icon>(element, null);

        result.Should().NotBeNull();
        result!.Provider.Should().Be(Icon.UrlProvider);
        result.Id.Should().Be("/static/NodeTypeIcons/document.svg");
    }

    [Fact]
    public void ConvertSingle_IconInstance_PassesThroughUnchanged()
    {
        // Regression: a properly-typed Icon (the existing wire/object form) must be untouched.
        var hub = GetHost();
        var icon = new Icon(Icon.FluentProvider, "Home");

        var result = hub.ConvertSingle<Icon>(icon, null);

        result.Should().BeSameAs(icon);
    }

    // ---- Issues #1657 / #1658: a raw STRING binding value must be read the way the layout layer -----
    // (and its own documentation) actually writes one. Both defects lived in ConvertString<T>, reached
    // from ConvertSingle's `string s =>` arm, which BlazorView.DataBind calls with no conversion:
    //   #1658  Enum.Parse(targetType, s) is case-SENSITIVE → ArgumentException on "center", while
    //          Stack.md's configuration table documents `"start"` / `"center"` / `"end"` for
    //          WithHorizontalAlignment. Observed in prod on Area Play/5.
    //   #1657  int.Parse(s) → FormatException on "8px", while Stack.md documents `"8px"` / `"1rem"` /
    //          `"16px"` for WithVerticalGap / WithHorizontalGap — which LayoutStackView binds into
    //          `int?`. Observed in prod on Area Play/4 (10 occurrences in 12 s).
    // Both threw out to DataBind, which logged `fail` and applied the default, so the documented value
    // was DROPPED on every render. The conversion must now read them, and stay TOTAL: an unreadable
    // string degrades to the default rather than faulting the DataBind observable.

    [Fact]
    public void ConvertSingle_LowerCaseEnumString_ResolvesCaseInsensitively()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<HorizontalAlignment>("center", null);

        result.Should().Be(HorizontalAlignment.Center,
            "the docs teach the lowercase form; a case-sensitive Enum.Parse rejected it (#1658)");
    }

    [Fact]
    public void ConvertSingle_MixedCaseEnumString_ResolvesCaseInsensitively()
    {
        var hub = GetHost();

        hub.ConvertSingle<HorizontalAlignment>("sTaRt", null).Should().Be(HorizontalAlignment.Start);
        hub.ConvertSingle<Orientation>("horizontal", null).Should().Be(Orientation.Horizontal);
    }

    [Fact]
    public void ConvertSingle_ExactCaseEnumString_StillResolves()
    {
        var hub = GetHost();

        hub.ConvertSingle<HorizontalAlignment>("End", null).Should().Be(HorizontalAlignment.End);
    }

    [Fact]
    public void ConvertSingle_LowerCaseEnumString_ToNullableEnum_Resolves()
    {
        // The skin properties are object? and the views bind them into nullable enums.
        var hub = GetHost();

        hub.ConvertSingle<HorizontalAlignment?>("center", null).Should().Be(HorizontalAlignment.Center);
    }

    [Fact]
    public void ConvertSingle_UnknownEnumString_ReturnsDefaultInsteadOfThrowing()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle("not-an-alignment", null, HorizontalAlignment.Right);

        result.Should().Be(HorizontalAlignment.Right,
            "a genuinely unknown literal degrades to the default — a throw would fault the DataBind stream");
    }

    [Fact]
    public void ConvertSingle_PxString_ToInt_StripsTheUnit()
    {
        var hub = GetHost();

        var result = hub.ConvertSingle<int>("8px", null);

        result.Should().Be(8, "\"8px\" is the documented WithHorizontalGap value and threw FormatException (#1657)");
    }

    [Fact]
    public void ConvertSingle_PxString_ToNullableInt_StripsTheUnit()
    {
        // LayoutStackView's VerticalGap/HorizontalGap are int? — the exact prod binding.
        var hub = GetHost();

        hub.ConvertSingle<int?>("16px", null).Should().Be(16);
    }

    [Fact]
    public void ConvertSingle_NegativePxString_ToInt_KeepsTheSign()
    {
        var hub = GetHost();

        hub.ConvertSingle<int>("-4px", null).Should().Be(-4);
    }

    [Fact]
    public void ConvertSingle_PercentString_ToInt_StripsTheUnit()
    {
        var hub = GetHost();

        hub.ConvertSingle<int>("50%", null).Should().Be(50);
    }

    [Fact]
    public void ConvertSingle_RemString_ToDouble_KeepsTheMagnitude()
    {
        var hub = GetHost();

        hub.ConvertSingle<double>("1.5rem", null).Should().Be(1.5,
            "the magnitude is read as authored — no root font size exists on this path, so no unit conversion is invented");
    }

    [Fact]
    public void ConvertSingle_FractionalCssLength_ToInt_Truncates()
    {
        // Same rule ConvertDoubleToInteger already applies to a bound double: 1.5 in an int slot is 1.
        var hub = GetHost();

        hub.ConvertSingle<int>("1.5rem", null).Should().Be(1);
    }

    [Fact]
    public void ConvertSingle_UpperCaseCssUnit_StripsTheUnit()
    {
        // CSS units are case-insensitive.
        var hub = GetHost();

        hub.ConvertSingle<int>("12PX", null).Should().Be(12);
    }

    [Fact]
    public void ConvertSingle_PlainNumberString_ToInt_StillWorks()
    {
        // Regression: the unit-stripping must not disturb a plain number.
        var hub = GetHost();

        hub.ConvertSingle<int>("42", null).Should().Be(42);
    }

    [Fact]
    public void ConvertSingle_DecimalString_ParsesInvariant_OnACommaDecimalThread()
    {
        // These strings arrive off the wire as JSON or CSS, where "1.5" is always one-and-a-half.
        // double.Parse(CurrentCulture) on a comma-decimal thread reads it as 15.
        var hub = GetHost();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            hub.ConvertSingle<double>("1.5", null).Should().Be(1.5);
            hub.ConvertSingle<double>("1.5rem", null).Should().Be(1.5);
            hub.ConvertSingle<int>("8px", null).Should().Be(8);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ConvertSingle_AbsurdlyLargeCssLength_ReturnsDefault_NeverAWrappedNumber()
    {
        // A fractional magnitude is truncated through a double, so the range check has to happen in
        // double space — where (double)long.MaxValue rounds UP. Without the 2^53 cap the cast wraps and
        // this returns long.MinValue: a wrong number, silently, instead of the default.
        var hub = GetHost();

        hub.ConvertSingle("9223372036854775808.5px", null, 7L).Should().Be(7L);
        hub.ConvertSingle("1e30px", null, 7).Should().Be(7);
    }

    [Fact]
    public void ConvertSingle_UnitOnlyKeyword_ReturnsDefaultInsteadOfThrowing()
    {
        // "auto"/"none" are legal CSS for a string slot but meaningless in a numeric one.
        var hub = GetHost();

        hub.ConvertSingle("auto", null, 7).Should().Be(7);
        hub.ConvertSingle("none", null, 7).Should().Be(7);
    }

    [Fact]
    public void ConvertSingle_UnrecognisedSuffix_ReturnsDefault_SoTheToleranceStaysNarrow()
    {
        // Only a RECOGNISED CSS unit is stripped: "8 apples" must not quietly become 8.
        var hub = GetHost();

        hub.ConvertSingle("8 apples", null, 7).Should().Be(7);
        hub.ConvertSingle("8apples", null, 7).Should().Be(7);
    }

    [Fact]
    public void ConvertSingle_UnreadableNumericString_ReturnsDefaultInsteadOfThrowing()
    {
        var hub = GetHost();

        hub.ConvertSingle("not-a-number", null, 99).Should().Be(99);
    }

    [Fact]
    public void ConvertSingle_UnsupportedTargetType_ReturnsDefaultInsteadOfThrowing()
    {
        // Previously an InvalidOperationException ("Cannot convert ... to ...") thrown from inside
        // DataBind's Select, which faults the observable and kills the binding for the view's lifetime.
        var hub = GetHost();
        var fallback = Guid.NewGuid();

        hub.ConvertSingle("whatever", null, fallback).Should().Be(fallback);
    }

    [Fact]
    public void ConvertSingle_BooleanAndDateTimeStrings_StillParse()
    {
        // Regression: the non-numeric branches keep working, and stop throwing on a bad value.
        var hub = GetHost();

        hub.ConvertSingle<bool>("true", null).Should().BeTrue();
        hub.ConvertSingle<bool>("TRUE", null).Should().BeTrue();
        hub.ConvertSingle("not-a-bool", null, true).Should().BeTrue("an unreadable bool degrades to the default");
        hub.ConvertSingle<DateTime>("2026-08-16T08:32:47Z", null).ToUniversalTime()
            .Should().Be(new DateTime(2026, 8, 16, 8, 32, 47, DateTimeKind.Utc));
        hub.ConvertSingle<char>("x", null).Should().Be('x');
    }
}
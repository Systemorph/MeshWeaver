using System;
using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;

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
}
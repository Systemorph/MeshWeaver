using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Domain;

/// <summary>
/// Displays the property as percentage with d decimal digits, and only allows values between min and max. The property is stored at the database according to its original type.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PercentageAttribute : ValidationAttribute
{
    /// <summary>
    /// 100% = 1, but not 100
    /// </summary>
    public double MinPercentage { get; set; } = 0;

    /// <summary>
    /// 100% = 1, but not 100
    /// </summary>
    public double MaxPercentage { get; set; } = 1;

    /// <summary>
    /// The number of decimal digits shown when the value is rendered as a percentage.
    /// </summary>
    public int DecimalDigits { get; set; } = 2;

    /// <summary>
    /// Validates that the value is a supported numeric type within the configured percentage range.
    /// </summary>
    /// <param name="value">The value to validate; <c>null</c> is treated as valid.</param>
    /// <param name="validationContext">Context describing the member being validated.</param>
    /// <returns><see cref="ValidationResult.Success"/> if valid; otherwise a result describing the failure.</returns>
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null) return ValidationResult.Success!;
        
        var valueType = value.GetType();

        if (!ValidationByType.TryGetValue(valueType, out var validation))
            return new ValidationResult(
                $"The {validationContext.MemberName} field must have type from these: {SupportedTypesList}.",
                new[] { validationContext.MemberName ?? "Unknown" }
            );

        if (!validation(value, MinPercentage, MaxPercentage))
            return new ValidationResult(
                $"The {validationContext.MemberName} field value should be in interval from {MinPercentage} to {MaxPercentage}.",
                new[] { validationContext.MemberName ?? "Unknown" }
            );

        return ValidationResult.Success!;
    }

    private static readonly IReadOnlyDictionary<
        Type,
        Func<object, double, double, bool>
    > ValidationByType = new Dictionary<Type, Func<object, double, double, bool>>
    {
        { typeof(double), DoubleIsValid },
        { typeof(decimal), DecimalIsValid },
        { typeof(float), FloatIsValid }
    };

    private static readonly string SupportedTypesList = string.Join(
        ", ",
        ValidationByType.Keys.Select(x => x.FullName)
    );

    private static bool FloatIsValid(object value, double minPercentage, double maxPercentage)
    {
        var percentage = (float)value;
        return percentage >= (float)minPercentage && percentage <= (float)maxPercentage;
    }

    private static bool DoubleIsValid(object value, double minPercentage, double maxPercentage)
    {
        var percentage = (double)value;
        return percentage >= minPercentage && percentage <= maxPercentage;
    }

    private static bool DecimalIsValid(object value, double minPercentage, double maxPercentage)
    {
        var percentage = (decimal)value;
        return percentage >= (decimal)minPercentage && percentage <= (decimal)maxPercentage;
    }
}

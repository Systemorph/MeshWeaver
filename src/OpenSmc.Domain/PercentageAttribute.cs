using System.ComponentModel.DataAnnotations;

namespace OpenSmc.Domain;

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

    public int DecimalDigits { get; set; } = 2;

    protected override ValidationResult IsValid(object value, ValidationContext validationContext)
    {
        var valueType = value.GetType();

        if (!ValidationByType.TryGetValue(valueType, out var validation))
            return new ValidationResult(
                $"The {validationContext.MemberName} field must have type from these: {SupportedTypesList}.",
                new[] { validationContext.MemberName }
            );

        if (!validation(value, MinPercentage, MaxPercentage))
            return new ValidationResult(
                $"The {validationContext.MemberName} field value should be in interval from {MinPercentage} to {MaxPercentage}.",
                new[] { validationContext.MemberName }
            );

        return ValidationResult.Success;
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

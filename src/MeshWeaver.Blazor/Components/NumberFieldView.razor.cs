using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Blazor form component for editing numeric values using a Fluent UI number field.
/// Supports integer and floating-point types; automatically configures step behaviour
/// based on whether <typeparamref name="TValue"/> is an integer-like type.
/// </summary>
/// <typeparam name="TValue">The numeric CLR type being edited (e.g. <c>int</c>, <c>decimal</c>, <c>double</c>).</typeparam>
public partial class NumberFieldView<TValue>
    where TValue:new()
{

    /// <summary>
    /// When true, spin buttons will not be rendered.
    /// </summary>
    public bool HideStep { get; set; }

    /// <summary>
    /// Allows associating a <see href="https://developer.mozilla.org/en-US/docs/Web/HTML/Element/datalist">datalist</see> to the element by <see href="https://developer.mozilla.org/en-US/docs/Web/API/Element/id">id</see>.
    /// </summary>
    public string? DataList { get; set; }

    /// <summary>
    /// Gets or sets the maximum length.
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>
    /// Gets or sets the minimum length.
    /// </summary>
    public int MinLength { get; set; }

    /// <summary>
    /// Gets or sets the size.
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// Gets or sets the amount to increase/decrease the number with. Only use whole number when TValue is int or long. 
    /// </summary>
    public string? Step { get; set; }

    /// <summary>
    /// Gets or sets the maximum value.
    /// </summary>
    public string? Max { get; set; }

    /// <summary>
    /// Gets or sets the minimum value.
    /// </summary>
    public string? Min { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="FluentInputAppearance" />.
    /// </summary>
    public FluentInputAppearance Appearance { get; set; } = FluentInputAppearance.Outline;

    /// <summary>
    /// Gets or sets the error message to show when the field can not be parsed.
    /// </summary>
    public string ParsingErrorMessage { get; set; } = "The {0} field must be a number.";

    private bool IsIntegerLike(Type type)
        => Nullable.GetUnderlyingType(type) is { } underlyingType
            ? IsIntegerType(underlyingType)
            : IsIntegerType(type);

    private bool IsIntegerType(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) ||
           type == typeof(byte) || type == typeof(sbyte) || type == typeof(uint) ||
           type == typeof(ulong) || type == typeof(ushort);
    /// <summary>
    /// Binds all number-field-specific parameters (HideStep, DataList, MaxLength, MinLength,
    /// Size, Step, Max, Min, Appearance, ParsingErrorMessage) from the view-model. The default
    /// for <c>HideStep</c> is derived from whether <typeparamref name="TValue"/> is integer-like,
    /// and <c>Step</c> defaults to <c>"any"</c> for floating-point types.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        DataBind(ViewModel.HideStep, x => x.HideStep, defaultValue: !IsIntegerLike(typeof(TValue)));
        DataBind(ViewModel.DataList, x => x.DataList);
        DataBind(ViewModel.MaxLength, x => x.MaxLength, defaultValue: int.MaxValue);
        DataBind(ViewModel.MinLength, x => x.MinLength);
        DataBind(ViewModel.Size, x => x.Size);
        DataBind(ViewModel.Step, x => x.Step, defaultValue:"any");
        DataBind(ViewModel.Max, x => x.Max);
        DataBind(ViewModel.Min, x => x.Min);
        DataBind(ViewModel.Appearance, x => x.Appearance);
        DataBind(ViewModel.ParsingErrorMessage, x => x.ParsingErrorMessage);

    }
}

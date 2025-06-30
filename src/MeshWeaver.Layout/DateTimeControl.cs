namespace MeshWeaver.Layout;

/// <summary>
/// Represents a date-time control with customizable properties.
/// </summary>
/// <param name="Data">The data associated with the date-time control.</param>
public record DateTimeControl(object Data) : FormControlBase<DateTimeControl>(Data), IFormControl
{
    /// <summary>
    /// Gets or initializes the minimum value for the date-time control.
    /// </summary>
    public object Min { get; init; }

    /// <summary>
    /// Gets or initializes the maximum value for the date-time control.
    /// </summary>
    public object Max { get; init; }

    /// <summary>
    /// Gets or initializes the appearance of the date-time control.
    /// </summary>
    public object Appearance { get; init; }

    /// <summary>
    /// Gets or initializes the name attribute of the date-time control.
    /// </summary>
    public object Name { get; init; }

    /// <summary>
    /// Gets or initializes the calendar view mode (Days, Months, Years).
    /// </summary>
    public object View { get; init; }

    /// <summary>
    /// Gets or initializes the culture for date formatting and localization.
    /// </summary>
    public object Culture { get; init; }

    /// <summary>
    /// Gets or initializes the day format (Numeric or TwoDigits).
    /// </summary>
    public object DayFormat { get; init; }

    /// <summary>
    /// Gets or initializes the date to set when double-clicking the text field.
    /// </summary>
    public object DoubleClickToDate { get; init; }

    /// <summary>
    /// Gets or initializes whether disabled dates are selectable.
    /// </summary>
    public object DisabledSelectable { get; init; }

    /// <summary>
    /// Gets or initializes whether to check all days when determining if months/years are disabled.
    /// </summary>
    public object DisabledCheckAllDaysOfMonthYear { get; init; }

    /// <summary>
    /// Gets or initializes the function to determine which dates should be disabled.
    /// Should be a string expression that evaluates to a function or a boolean for simple cases.
    /// </summary>
    public object DisabledDateFunc { get; init; }

    /// <summary>
    /// Gets or initializes the horizontal position of the calendar popup.
    /// </summary>
    public object PopupHorizontalPosition { get; init; }

    /// <summary>
    /// Gets or initializes whether the input should automatically focus when the page loads.
    /// </summary>
    public new object AutoFocus { get; init; }

    /// <summary>
    /// Gets or initializes whether the date picker is read-only.
    /// </summary>
    public new object Readonly { get; init; }

    /// <summary>
    /// Gets or initializes whether the date picker is required.
    /// </summary>
    public new object Required { get; init; }

    /// <summary>
    /// Gets or initializes the ARIA label for accessibility.
    /// </summary>
    public object AriaLabel { get; init; }

    /// <summary>
    /// Gets or initializes the CSS class for styling.
    /// </summary>
    public new object Class { get; init; }

    /// <summary>
    /// Gets or initializes the HTML ID attribute.
    /// </summary>
    public new object Id { get; init; }
}

using System.Globalization;
using CalendarViews = Microsoft.FluentUI.AspNetCore.Components.CalendarViews;
using DayFormat = Microsoft.FluentUI.AspNetCore.Components.DayFormat;
using FluentInputAppearance = Microsoft.FluentUI.AspNetCore.Components.FluentInputAppearance;
using HorizontalPosition = Microsoft.FluentUI.AspNetCore.Components.HorizontalPosition;

namespace MeshWeaver.Blazor.Components;

public partial class DateTimeView
{
    private FluentInputAppearance Appearance { get; set; } = FluentInputAppearance.Outline;
    private CalendarViews View { get; set; } = CalendarViews.Days;
    private CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    private DayFormat? DayFormat { get; set; }
    private DateTime? DoubleClickToDate { get; set; }
    private bool DisabledSelectable { get; set; } = true;
    private bool DisabledCheckAllDaysOfMonthYear { get; set; }
    private Func<DateTime, bool> DisabledDateFunc { get; set; }
    private HorizontalPosition? PopupHorizontalPosition { get; set; }
    private string Name { get; set; }

    // Properties for Min/Max date handling
    private DateTime? MinDate { get; set; }
    private DateTime? MaxDate { get; set; }
    private string DisabledDateExpression { get; set; }
    private string AriaLabel { get; set; }

    protected override void BindData()
    {
        base.BindData();

        if (ViewModel != null)
        {
            DataBind(ViewModel.Appearance, x => x.Appearance, defaultValue: FluentInputAppearance.Outline);
            DataBind(ViewModel.View, x => x.View, defaultValue: CalendarViews.Days);
            DataBind(ViewModel.Culture, x => x.Culture, defaultValue: CultureInfo.CurrentCulture);
            DataBind(ViewModel.DayFormat, x => x.DayFormat);
            DataBind(ViewModel.DoubleClickToDate, x => x.DoubleClickToDate);
            DataBind(ViewModel.DisabledSelectable, x => x.DisabledSelectable, defaultValue: true);
            DataBind(ViewModel.DisabledCheckAllDaysOfMonthYear, x => x.DisabledCheckAllDaysOfMonthYear);
            DataBind(ViewModel.PopupHorizontalPosition, x => x.PopupHorizontalPosition);
            DataBind(ViewModel.Name, x => x.Name);

            // Handle Min/Max dates by creating a disabled date function
            DataBind(ViewModel.Min, x => x.MinDate);
            DataBind(ViewModel.Max, x => x.MaxDate);
            DataBind(ViewModel.AriaLabel, x => x.AriaLabel);
            DataBind(ViewModel.DisabledDateFunc, x => x.DisabledDateExpression);

            // Create the disabled date function that combines min/max with custom logic
            DisabledDateFunc = CreateDisabledDateFunction();
        }
    }

    private Func<DateTime, bool> CreateDisabledDateFunction()
    {
        // If no constraints are set, return null function (no dates disabled)
        if (MinDate == null && MaxDate == null && string.IsNullOrEmpty(DisabledDateExpression))
            return null;

        return date =>
        {
            // Check min date constraint
            if (MinDate.HasValue && date < MinDate.Value.Date)
                return true;

            // Check max date constraint  
            if (MaxDate.HasValue && date > MaxDate.Value.Date)
                return true;

            // TODO: If there's a custom disabled date expression, evaluate it here
            // For now, we'll keep it simple and just use min/max
            // In a real implementation, you might want to use a more sophisticated
            // expression evaluator or allow delegates to be passed

            return false;
        };
    }
}

using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor
{
    public partial class NumberFieldView<TValue>
        where TValue:new()
    {
        private TValue Number { get; set; }


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

        public int MaxLength { get; set; } = 14;

        /// <summary>
        /// Gets or sets the minimum length.
        /// </summary>

        public int MinLength { get; set; } = 1;

        /// <summary>
        /// Gets or sets the size.
        /// </summary>

        public int Size { get; set; } = 20;

        /// <summary>
        /// Gets or sets the amount to increase/decrease the number with. Only use whole number when TValue is int or long. 
        /// </summary>

        public string Step { get; set; }

        /// <summary>
        /// Gets or sets the maximum value.
        /// </summary>

        public string Max { get; set; }

        /// <summary>
        /// Gets or sets the minimum value.
        /// </summary>

        public string Min { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="AspNetCore.Components.Appearance" />.
        /// </summary>

        public FluentInputAppearance Appearance { get; set; } = FluentInputAppearance.Outline;

        /// <summary>
        /// Gets or sets the error message to show when the field can not be parsed.
        /// </summary>

        public string ParsingErrorMessage { get; set; } = "The {0} field must be a number.";



        protected override void BindData()
        {
            base.BindData();
            DataBind(ViewModel.Data, x => x.Number);
            DataBind(ViewModel.HideStep, x => x.HideStep);
            DataBind(ViewModel.DataList, x => x.DataList);
            DataBind(ViewModel.MaxLength, x => x.MaxLength);
            DataBind(ViewModel.MinLength, x => x.MinLength);
            DataBind(ViewModel.Size, x => x.Size);
            DataBind(ViewModel.Step, x => x.Step);
            DataBind(ViewModel.Max, x => x.Max);
            DataBind(ViewModel.Min, x => x.Min);
            DataBind(ViewModel.Appearance, x => x.Appearance);
            DataBind(ViewModel.ParsingErrorMessage, x => x.ParsingErrorMessage);

        }

    }
}

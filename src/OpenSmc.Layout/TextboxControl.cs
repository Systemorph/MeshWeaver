namespace OpenSmc.Layout
{
    public record TextBoxControl(object Data)
        : UiControl<TextBoxControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
    {
        public object Placeholder { get; init; }

        public object IconStart { get; init; }

        public object IconEnd { get; init; }

        public object Autocomplete { get; init; }

        public object Immediate { get; init; }

        public object ImmediateDelay {get; init; }

        public TextBoxControl WithPlaceholder(object placeholder) => this with { Placeholder = placeholder };

        public TextBoxControl WithIconStart(object icon) => this with { IconStart = icon };

        public TextBoxControl WithIconEnd(object icon) => this with { IconEnd = icon };

        public TextBoxControl WithAutocomplete(object autocomplete) => this with { Autocomplete = autocomplete };

        public TextBoxControl WithImmediate(object immediate) => this with { Immediate = immediate };
    
        public TextBoxControl WithImmediateDelay(object immediateDelay) => this with { ImmediateDelay = immediateDelay };
    }
}

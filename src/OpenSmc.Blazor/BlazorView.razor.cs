using OpenSmc.Data;
using OpenSmc.Layout;

namespace OpenSmc.Blazor
{
    public partial class BlazorView<TViewModel> : IDisposable
    {
        protected override void OnParametersSet()
        {
            base.OnParametersSet();
            BindDataContext();
        }

        protected object BindProperty(object instance, string propertyName)
        {
            if(instance == null)
                return null;

            var type = instance.GetType();
            var property = type.GetProperty(propertyName);
            if(property == null)
                return null;
            return property.GetValue(instance, null);
        }

        protected object DataContext { get; set; }

        public void BindDataContext()
        {
            if (ViewModel is UiControl control)
                if (control.DataContext is WorkspaceReference reference)
                    Disposables.Add(Stream
                        .Reduce(reference)
                        .Subscribe(v =>
                        {
                            DataContext = v;
                            InvokeAsync(StateHasChanged);
                        }));
                else
                    DataContext = control.DataContext;
        }

        protected List<IDisposable> Disposables { get; } = new();

        public void Dispose()
        {
            foreach (var d in Disposables)
            {
                d.Dispose();
            }
        }
    }
}

using OpenSmc.Layout;

namespace OpenSmc.Blazor;

public class LayoutAreaPageState
{
    public object Address { get; private set; }
    public LayoutAreaReference Reference { get; private set; }

    public event Action OnChange;

    public void SetAddressAndReference(object address, LayoutAreaReference reference)
    {
        Address = address;
        Reference = reference;
        OnChange?.Invoke();
    }
}

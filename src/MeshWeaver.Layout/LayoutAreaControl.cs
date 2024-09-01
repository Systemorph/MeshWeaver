using MeshWeaver.Utils;

namespace MeshWeaver.Layout;

public record LayoutAreaControl(object Address, LayoutAreaReference Reference)
    : UiControl<LayoutAreaControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    public object DisplayArea { get; init; } = Reference.Area.Wordify();
    public object ShowProgress { get; init; } = true;

    public LayoutAreaControl WithDisplayArea(string displayArea) => this with { DisplayArea = displayArea };

    public virtual bool Equals(LayoutAreaControl other)
    {
        if(other is null) return false;
        if(ReferenceEquals(this, other)) return true;

        return base.Equals(other)
               && DisplayArea == other.DisplayArea
               && ShowProgress == other.ShowProgress 
               && Equals(Reference, other.Reference)
               && JsonObjectEqualityComparer.Instance.Equals(Address, other.Address)
               ;
    }

    public override string ToString()
    {
        return Reference.ToAppHref(Address);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            base.GetHashCode(),
            DisplayArea,
            ShowProgress,
            Reference,
            Address.ToString()
        );
    }
}

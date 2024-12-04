using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Layout
{
    public record ItemTemplateControl(UiControl View, object Data) :
        UiControl<ItemTemplateControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        public static string ViewArea = nameof(View);


        public Orientation? Orientation { get; init; }

        public bool Wrap { get; init; }

        public ItemTemplateControl WithOrientation(Orientation orientation) => this with { Orientation = orientation };

        public ItemTemplateControl WithWrap(bool wrap) => this with { Wrap = wrap };
        protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
        {
            var ret = base.Render(host, context, store);
            var renderedView = host.RenderArea(GetContextForArea(context, ItemTemplateControl.ViewArea), View, ret.Store);
            return renderedView with { Updates = ret.Updates.Concat(renderedView.Updates) };
        }

        public virtual bool Equals(ItemTemplateControl other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (!base.Equals(other))
                return false;
            return Equals(View, other.View)
                   && Equals(Wrap, other.Wrap)
                   && Equals(Orientation, other.Orientation)
                   && LayoutHelperExtensions.DataEquality(Data, other.Data);
        }

        public override int GetHashCode() => 
            HashCode.Combine(base.GetHashCode(),
                Wrap,
                View,
                Orientation,
                LayoutHelperExtensions.DataHashCode(Data)
            );
    }
}

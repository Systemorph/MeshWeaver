using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Demo.ViewModel.Listbox;

public static class ListboxDemoArea
{
    public static LayoutDefinition AddListboxDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(BaseColorListArea.BaseColorList), BaseColorListArea.BaseColorList)
            .WithNavMenu((menu, _) => menu
                .WithNavLink(
                    "Raw: Listbox Control",
                    new LayoutAreaReference(nameof(BaseColorListArea.BaseColorList)).ToHref(layout.Hub.Address),
                    FluentIcons.Grid
                )
            );
}

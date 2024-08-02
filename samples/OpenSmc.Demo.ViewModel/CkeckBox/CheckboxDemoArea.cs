using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Demo.ViewModel.CkeckBox;

public static class CheckboxDemoArea
{
    public static LayoutDefinition AddCheckboxDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(TermsAgreementTickArea.TermsTick), TermsAgreementTickArea.TermsTick)
            .WithNavMenu((menu, _) => menu
                .WithNavLink(
                    "Raw: Checkbox Control",
                    new LayoutAreaReference(nameof(TermsAgreementTickArea.TermsTick)).ToHref(layout.Hub.Address),
                    FluentIcons.Person
                )
            );
}

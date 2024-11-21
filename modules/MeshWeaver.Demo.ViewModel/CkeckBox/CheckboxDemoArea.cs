﻿using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.Demo.ViewModel.CkeckBox;

public static class CheckboxDemoArea
{
    public static LayoutDefinition AddCheckboxDemo(this LayoutDefinition layout)
        => layout.WithView(nameof(TermsAgreementTickArea.TermsTick), TermsAgreementTickArea.TermsTick)
            .WithNavMenu((menu, _, _) => menu
                .WithNavLink(
                    "Raw: Checkbox Control",
                    new LayoutAreaReference(nameof(TermsAgreementTickArea.TermsTick)).ToAppHref(layout.Hub.Address),
                    FluentIcons.Person
                )
            );
}

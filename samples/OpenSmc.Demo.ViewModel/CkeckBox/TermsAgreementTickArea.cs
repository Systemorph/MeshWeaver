using System.Reactive.Linq;
using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Demo.ViewModel.CkeckBox;

public static class TermsAgreementTickArea
{
    public static object TermsTick(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack
            .WithVerticalGap(16)
            .WithView(
                (_, _) =>
                    Template.Bind(
                        new AgreementTick(false),
                        nameof(AgreementTick),
                        at => Controls.CheckBox("I agree with the Terms and Conditions", at.Signed)
                    )
            )
            .WithView(
                nameof(SubmitAgreement),
                (a, _) => a
                    .GetDataStream<AgreementTick>(nameof(AgreementTick))
                    .Select(at => SubmitAgreement(at.Signed))
            )
        ;

    private static object SubmitAgreement(bool signed)
        => Controls.Button("Submit")
            .WithIconStart(FluentIcons.Edit)
            .WithDisabled(!signed);
}

internal record AgreementTick(bool Signed);

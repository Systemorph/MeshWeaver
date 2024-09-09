using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Demo.ViewModel.CkeckBox;

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
            .WithView((a, _) => a
                .GetDataStream<AgreementTick>(nameof(AgreementTick))
                .Select(at => SubmitAgreement(at.Signed)), nameof(SubmitAgreement))
        ;

    private static object SubmitAgreement(bool signed)
        => Controls.Button("Submit")
            .WithIconStart(FluentIcons.Edit)
            .WithDisabled(!signed);
}

internal record AgreementTick(bool Signed);

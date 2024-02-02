import { makeBadge } from "@open-smc/sandbox/src/Badge";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";

const itemTemplate =
    makeItemTemplate()
        .withView(
            makeBadge()
                .withTitle(makeBinding("item.title"))
                .withSubtitle(makeBinding("item.subtitle"))
                .withTooltip(makeBinding("item.tooltip"))
                .withColor(makeBinding("item.color"))
                .build()
        )
        .withFlex()
        .withData(
            [
                {
                    title: "SUB",
                    subtitle: "2/5",
                    color: "#5BC0DE",
                    tooltip: "Submission"
                },
                {
                    title: "REV",
                    subtitle: "0/3",
                    color: "#0171FF",
                    tooltip: "Review"
                },
                {
                    title: "SGN",
                    subtitle: "1/5",
                    color: "#A25BDE",
                    tooltip: "Sign off"
                },
                {
                    title: "CPL",
                    subtitle: "1/5",
                    color: "#03CB5D",
                    tooltip: "Complete"
                },
            ]
        )
        .build();

export function BadgePage() {
    return (
        <div>
            <div>
                <h3>Sample badges</h3>
                <Sandbox root={itemTemplate}/>
            </div>
        </div>
    );
}
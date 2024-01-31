import { makeIcon } from "@open-smc/sandbox/Icon";
import { Sandbox } from "@open-smc/sandbox/Sandbox";
import { makeItemTemplate } from "@open-smc/sandbox/ItemTemplate";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";

const itemTemplate =
    makeItemTemplate()
        .withView(
            makeIcon(makeBinding("item.icon"))
                .withColor(makeBinding("item.color"))
                .withSize(makeBinding("item.size"))
                .withBackground(makeBinding("item.background"))
                .withBorderRadius(makeBinding("item.borderRadius"))
                .build()
        )
        .withFlex(style=> style.withGap('12px').withAlignItems("center"))
        .withData(
            [
                { icon: "briefcase"},
                { icon: "logs", color: "#03CB5D", size: 'S'},
                { icon: "user", color: "#A25BDE", size: 'M'},
                { icon: "trash", color: "#5BC0DE", size: 'L'},
                { icon: "logs", color: "#03CB5D", size: 'S', background: true},
                { icon: "user", color: "#A25BDE", size: 'M', background: true, borderRadius: 'rounded'},
                { icon: "trash", color: "#5BC0DE", size: 'L', background: true, borderRadius: 'circle'},
                { icon: "trash", color: "#5BC0DE", size: 'XL', background: true, borderRadius: 'rounded'},
                { icon: "trash", color: "#5BC0DE", size: 'XXL', background: true, borderRadius: 'circle'},
            ]
        )
        .build();

export function IconPage() {
    return (
        <div>
            <h2>Sample icons</h2>
            <Sandbox root={itemTemplate}/>
        </div>
    );
}

import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import styles from "./menuItemPage.module.scss";
import { AreaChangedEvent } from "@open-smc/application/src/application.contract";
import { makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { ExpandAction } from "@open-smc/sandbox/src/ExpandableControl";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { useMemo, useState } from "react";
import { InputText } from "@open-smc/ui-kit/src/components/InputText";

const expandPromotionMenu: ExpandAction = ({area}, menuItem) => {
    const promotions = ["Submission", "Review", "Sign-off", "Complete"];

    const areas = promotions.map(promotion => {
        const menuItem = makeMenuItem()
            .withTitle(promotion)
            .withClickAction(payload => alert(`Promote to ${payload}`), promotion)
            .build();
        return new AreaChangedEvent(promotion, menuItem);
    });

    const stack = makeStack(areas).build();

    menuItem.setArea(area, stack);
}

const data = [
    { title: "Home", icon: "sm-home"},
    { title: "Business processes", icon: "sm-briefcase", color: "#0171ff"},
    { title: "Logs", icon: "sm-logs", color: "#03CB5D"},
    { title: "Profile", icon: "sm-user", color: "#A25BDE"},
    { title: "Trash", icon: "sm-trash", color: "#5BC0DE"},
];

const basic = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item.title"))
            .withColor(makeBinding("item.color"))
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px"))
    .build();

const withIcon = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item.title"))
            .withColor(makeBinding("item.color"))
            .withIcon(makeBinding("item.icon"))
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px"))
    .build();

const expandable = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item.title"))
            .withColor(makeBinding("item.color"))
            .withIcon(makeBinding("item.icon"))
            .withExpandAction(expandPromotionMenu, makeBinding("item.title"))
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px"))
    .build();

const iconOnly = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withColor(makeBinding("item.color"))
            .withIcon(makeBinding("item.icon"))
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px"))
    .build();

const largeButton = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item.title"))
            .withColor(makeBinding("item.color"))
            .withIcon(makeBinding("item.icon"))
            .withSkin("LargeButton")
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px").withFlexWrap("wrap"))
    .build();

const largeIcon = makeItemTemplate()
    .withView(
        makeMenuItem()
            .withTitle(makeBinding("item.title"))
            .withColor(makeBinding("item.color"))
            .withIcon(makeBinding("item.icon"))
            .withClickAction(payload => alert(payload), makeBinding("item.title"))
            .build()
    )
    .withData(data)
    .withFlex(style => style.withGap("10px"))
    .build();

export function MenuItemPage() {
    const [width, setWidth] = useState("120px");

    const withCustomWidth = useMemo(() => {
        return makeItemTemplate()
            .withView(
                makeMenuItem()
                    .withTitle(makeBinding("item.title"))
                    .withColor(makeBinding("item.color"))
                    .withIcon(makeBinding("item.icon"))
                    .withClickAction(payload => alert(payload), makeBinding("item.title"))
                    .withStyle(style => style.withWidth(width))
                    .build()
            )
            .withData(data)
            .withSkin("VerticalPanel")
            .build();
    }, [width]);


    return (
        <div className={styles.container}>
            <div>
                <h3>Basic</h3>
                <Sandbox root={basic} log={true}/>
            </div>
            <div>
                <h3>With icon</h3>
                <Sandbox root={withIcon} log={true}/>
            </div>
            <div>
                <h3>Expandable</h3>
                <Sandbox root={expandable} log={true}/>
            </div>
            <div>
                <h3>Icon only</h3>
                <Sandbox root={iconOnly}/>
            </div>
            <div>
                <h3>Icon and title</h3>
                <Sandbox root={withIcon}/>
            </div>
            <div>
                <h3>Large button</h3>
                <Sandbox root={largeButton}/>
            </div>
            <div>
                <h3>Large icon</h3>
                <Sandbox root={largeIcon}/>
            </div>
            <div>
                <h3>Custom width</h3>
                <div>
                    <InputText value={width} onChange={event => setWidth(event.target.value)}/>
                </div>
                <Sandbox root={withCustomWidth}/>
            </div>
        </div>
    );
}
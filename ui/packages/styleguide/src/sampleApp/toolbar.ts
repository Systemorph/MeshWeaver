import { makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { openDataPanel } from "./dataPanel";
import { openOrsaPanel } from "./orsaPanel";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeBadge } from "@open-smc/sandbox/src/Badge";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { modal } from "./modalSample/modal";
import { openModalWindow } from "./sampleApp";

const data = [
    {
        title: "REV",
        subtitle: "1/3",
        color: "#5BC0DE"
    },
    {
        title: "SGN",
        subtitle: "1/3",
        color: "#0171FF"
    },
    {
        title: "CMP",
        subtitle: "1/3",
        color: "#A25BDE"
    },
]

const badges = makeItemTemplate()
    .withView(
        makeBadge()
            .withTitle(makeBinding("item.title"))
            .withSubtitle(makeBinding("item.subtitle"))
            .withColor(makeBinding("item.color"))
            .build()
    )
    .withData(data);

const orsaMenuItem =
    makeMenuItem()
        .withTitle("ORSA Process")
        .withIcon("sm-briefcase")
        .withClickAction(openOrsaPanel)
        .withIcon("sm sm-briefcase");

const leftSide = makeStack()
    .withView(
        makeMenuItem()
            .withTitle("Data")
            .withIcon("database")
            .withClickAction(openDataPanel)
    )
    .withView(
        makeMenuItem()
            .withTitle("Dialog S")
            .withClickAction(() => openModalWindow(modal, {size: "S"}))
    )
    .withView(
        makeMenuItem()
            .withTitle("Dialog M")
            .withClickAction(() => openModalWindow(modal, {size: "M"}))
    )
    .withView(
        makeMenuItem()
            .withTitle("Dialog L")
            .withClickAction(() => openModalWindow(modal, {isClosable: false, size: "L"}))
    )

const rightSide = makeStack()
    .withView(badges)
    .withView(orsaMenuItem);

export const toolbar = makeStack()
    .withView(leftSide)
    .withView(rightSide)
    .withSkin("Toolbar")
    .withStyle(style =>
        style.withJustifyContent("space-between")
    );

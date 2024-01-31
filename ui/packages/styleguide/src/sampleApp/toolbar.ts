import { makeStack } from "@open-smc/sandbox/LayoutStack";
import { makeMenuItem } from "@open-smc/sandbox/MenuItem";
import { openDataPanel } from "./dataPanel";
import { openOrsaPanel } from "./orsaPanel";
import { makeItemTemplate } from "@open-smc/sandbox/ItemTemplate";
import { makeBadge } from "@open-smc/sandbox/Badge";
import { makeBinding } from "@open-smc/application/dataBinding/resolveBinding";
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

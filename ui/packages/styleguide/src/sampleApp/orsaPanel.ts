import { makeStack } from "@open-smc/sandbox/src/LayoutStack";
import { makeHtml } from "@open-smc/sandbox/src/Html";
import { makeMenuItem } from "@open-smc/sandbox/src/MenuItem";
import { makeItemTemplate } from "@open-smc/sandbox/src/ItemTemplate";
import { makeActivity } from "@open-smc/sandbox/src/Activity";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { openContextPanel } from "./sampleApp";
import { chance } from "../chance";
import { makeIcon } from "@open-smc/sandbox/src/Icon";
import { brandeisBlue, brightGray } from "@open-smc/application/src/colors";
import { makeCheckbox } from "@open-smc/sandbox/src/Checkbox";
import { v4 } from "uuid";
import { sum } from "lodash";
import {
    makePromotionDialogs,
    promotionFinishedSuccessfully,
    promotionStart,
    promotionValidated, subToRevError, subToRevSuccess, subToRevSuccessWithWarnings
} from "./orsaProcessSteps";

export function openOrsaPanel() {
    openContextPanel(
        makeHtml("<h2>ORSA Process</h2>"),
        makeStack()
            .withView(makeLatestActivity())
            .withView(makeActions())
    )
}

function makeLatestActivity() {
    return makeStack()
        .withView(
            makeStack()
                .withView(makeHtml("<h3>Latest activity</h3>"))
                .withView(
                    makeMenuItem()
                        .withTitle("View more")
                        .withSkin("Link")
                        .withClickAction(() =>
                            openAllActivities()
                        )
                )
                .withSkin("HorizontalPanel")
                .withFlex(style => style.withJustifyContent("space-between"))
        )
        .withView(makeActivities(3))
        .withStyle(style => style.withMargin("0 0 32px"));
}

function openAllActivities() {
    openContextPanel(
        makeStack()
            .withView(
                makeMenuItem()
                    .withIcon("chevron-left")
                    .withClickAction(() => openOrsaPanel())
            )
            .withView(makeHtml("<h2>Activity</h2>"))
            .withSkin("HorizontalPanel"),
        makeActivities()
    )
}

function makeActivities(count?: number) {
    return makeItemTemplate()
        .withView(
            makeActivity()
                .withUser(makeBinding("item.user"))
                .withColor(makeBinding("item.color"))
                .withTitle(makeBinding("item.summary"))
                .withDate(makeBinding("item.date"))
                .withClickAction(() => alert("clicked"))
                .build()
        )
        .withData(count ? activities.slice(0, count) : activities)
        .withSkin("VerticalPanel")
        .withStyle(style => style.withGap("1px"));
}

function makeActions() {
    return makeStack()
        .withView(makeHtml("<h3 class='heading'>Actions</h3"))
        .withView(
            makeItemTemplate()
                .withView(
                    makeStack()
                        .withView(
                            makeIcon(makeBinding("item.icon"))
                                .withColor(makeBinding("item.color"))
                                .withSize("L")
                        )
                        .withView(
                            makeHtml(makeBinding("item.html"))
                        )
                        .withView(
                            makeMenuItem()
                                .withTitle(makeBinding("item.title"))
                                .withColor(brandeisBlue)
                                .withClickAction(
                                    payload => {
                                        switch (payload) {
                                            case 1:
                                                subToRevSuccessWithWarnings()
                                                break;
                                            case 2:
                                                subToRevError();
                                                break;
                                            default:
                                                subToRevSuccess();
                                                break;
                                        }
                                    },
                                    makeBinding("index")
                                )
                                .withStyle(style => style.withWidth("125px"))
                        )
                        .withSkin("Action")
                    // .withStyle(style => style.withJustifyContent("space-between"))
                        .build()
                )
                .withData(actions)
                .withSkin("VerticalPanel")
                .withStyle(style => style.withGap("16px"))
        );
}

const colors = ["#0171FF", "#03CB5D", "#5BC0DE", "#A25BDE"];

const users = [
    {displayName: chance.name({gender: "male"}), photo: "av-user1.png"},
    {displayName: chance.name({gender: "female"}), photo: "av-user2.png"},
    {displayName: chance.name({gender: "female"}), photo: "av-user3.png"},
    {displayName: chance.name({gender: "male"}), photo: "av-user4.png"},
]

const summaries = [
    "Complete Submission for BaseCase, ORSA1",
    "Finished review on ORSA1",
    "Signed off on ORSA3",
]

const activities = chance.n(() => {
    return {
        user: chance.pickone(users),
        date: chance.date({year: new Date().getFullYear()}),
        summary: chance.pickone(summaries),
        color: chance.pickone(colors),
    }
}, 20);

const actions = [
    {
        title: "Sign off",
        icon: "sm-edit",
        color: "#0171FF",
        html: "<strong>Verify reviewed figures</strong> <em>ORSA1</em>"
    },
    {
        title: "Finish review",
        icon: "sm-thumbs-up",
        color: "#03CB5D",
        html: "<strong>Check submitted data</strong> <em>ORSA3</em>"
    },
    {
        title: "Reopen",
        icon: "sm-undo",
        color: "#5BC0DE",
        html: "<strong>New data to submit</strong> <em>ORSA1, ORSA2, ...</em>"
    },
    {
        title: "Kick off",
        icon: "sm-database",
        color: "#A25BDE",
        html: "<strong>Start data collection</strong> <em>ORSA3</em>"
    },
]
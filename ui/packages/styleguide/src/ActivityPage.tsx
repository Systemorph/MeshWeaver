import { makeActivity } from "@open-smc/sandbox/src/Activity";
import { Sandbox } from "@open-smc/sandbox/src/Sandbox";
import { makeBinding } from "@open-smc/application/src/dataBinding/resolveBinding";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import { useState } from "react";
import { chance } from "./chance";
import {LayoutStack, makeStack} from "@open-smc/sandbox/src/LayoutStack";
import {makeMenuItem} from "@open-smc/sandbox/src/MenuItem";
import { v4 } from "uuid";
import BlockUi from "@availity/block-ui";
import "@availity/block-ui/dist/index.css";

export function ActivityPage() {
    const [stack, setStack] = useState(makeActivityStack(3));
    const [blocked, setBlocked] = useState(false);
    return (
        <div>
            <div>
                <Button label={"Block"} onClick={() => {setBlocked(true)}}></Button>
                <Button label={"Unblock"} onClick={() => {setBlocked(false)}}></Button>
                <Button label={"Add item "} onClick={() => {
                    const area = v4();
                    stack.addView(makeItemWithActivity(stack,area), (builder) => builder.withArea(area));
                }
                } />
                <Button label={"Reset"} onClick={() => setStack(makeActivityStack(3))}/>
                <BlockUi blocking={blocked} message={""} loader={null} >
                    <Sandbox root={stack}/>
                </BlockUi>
            </div>
        </div>
    );
}

const summaries = [
    "Finished the review on ORSA 1",
    "Signed off on ORSA 1",
];

const colors = ["#0171FF", "#03CB5D", "#5BC0DE", "#A25BDE"];

function getData() {
    return chance.n(getDataItem, 5);
}

function getDataItem() {
    const user = {displayName: chance.name(), email: ''};
    const date = chance.date();
    const color = chance.pickone(colors);
    const summary = chance.pickone(summaries);

    return {
        user,
        date,
        color,
        summary
    }
}

function makeActivityStack(activityNumber: number) {
    const stack = makeStack().withSkin("VerticalPanel")
        .withHighlightNewAreas(true)
        .withStyle(style => style.withGap("1px"));
    const stackInstance = stack.build();
    for(let i=0; i < activityNumber; i++){
        const area = v4();
        stackInstance.addView(
            makeItemWithActivity(stackInstance, area), (builder) => {builder.withArea(area)}
        )
    }

    return stackInstance;
}

function makeItem() {
    const data = getDataItem();

    return makeActivity()
        .withDataContext(data)
        .withUser(makeBinding("user"))
        .withDate(makeBinding("date"))
        .withTitle(makeBinding("summary"))
        .withColor(makeBinding("color"));
}

function makeItemWithActivity(stackInstance: LayoutStack, area: string) {
    return makeStack()
        .withSkin('HorizontalPanel')
        .withView(makeItem())
        .withView(makeStack()
            .withSkin('HorizontalPanel')
            .withView(makeMenuItem()
                .withTitle('Add next')
                .withClickAction(()=> {
                    const newArea = v4();
                    stackInstance.addView(makeItemWithActivity(stackInstance, newArea), (builder) => {
                    builder
                        .withArea(newArea)
                        .withOptions({insertAfter: area})
                })})
            )
            .withView(makeMenuItem()
                .withTitle('remove')
                .withClickAction(()=> stackInstance.removeView(area))
            ))
}
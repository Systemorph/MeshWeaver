import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import { isOfType, ofType } from "../contract/ofType";
import {
    DataChangedEvent,
    EntireWorkspace,
    LayoutAreaReference,
    SubscribeDataRequest
} from "@open-smc/data/src/data.contract";
import { makeBinding } from "../dataBinding/resolveBinding";
import { messageOfType } from "@open-smc/message-hub/src/operators/messageOfType";

// this is mock

export const dataSyncHub =
    new SubjectHub((input, output) => {
        input.pipe(messageOfType(SubscribeDataRequest))
            .subscribe(({message}) => {
                const {id, workspaceReference} = message;

                if (isOfType(workspaceReference, EntireWorkspace)) {
                    const message = new DataChangedEvent(id, workspace);
                    output.next({message});
                }

                if (isOfType(workspaceReference, LayoutAreaReference)) {
                    const message = new DataChangedEvent(id, layout1);
                    output.next({message});
                }
            });
    });

const workspace = {
    foo: 1
}

const simpleLayout = {
    $type: "LayoutArea",
    id: "/",
    style: {},
    options: {},
    control: {
        $type: "MenuItemControl",
        title: "Hello world",
        icon: "systemorph-fill"
    }
}

const layout1= {
    $type: "OpenSmc.Layout.LayoutArea",
    id: "/",
    style: {},
    options: {},
    control: {
        $type: "OpenSmc.Layout.Composition.LayoutStackControl",
        skin: "MainWindow",
        areas: [
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/Main",
                control: {
                    $type: "OpenSmc.Layout.Views.MenuItemControl",
                    title: "Hello world",
                    icon: "systemorph-fill"
                }
            },
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/Toolbar",
                control: {
                    $type: "OpenSmc.Layout.TextBoxControl",
                    dataContext: {
                        value: "Hello world",
                        // ref: new WorkspaceReference(), // always reference to the main store
                    },
                    value: "123"
                    // value: makeBinding("$.value") // json path
                }
            }
        ]
    }
}


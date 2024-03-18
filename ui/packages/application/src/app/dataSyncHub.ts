import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import { isOfContractType, ofContractType } from "../contract/ofContractType";
import {
    DataChangedEvent,
    EntireWorkspace,
    LayoutAreaReference,
    SubscribeDataRequest
} from "@open-smc/data/src/data.contract";
import { makeBinding } from "../dataBinding/resolveBinding";

// this is mock

export const dataSyncHub =
    new SubjectHub((input, output) => {
        input.pipe(ofContractType(SubscribeDataRequest))
            .subscribe(({message}) => {
                const {workspaceReference} = message;

                if (isOfContractType(workspaceReference, EntireWorkspace)) {
                    const message = new DataChangedEvent(workspace);
                    output.next({message});
                }

                if (isOfContractType(workspaceReference, LayoutAreaReference)) {
                    const message = new DataChangedEvent(layout);
                    output.next({message});
                }
            });
    });

const workspace = {

}

const layout= {
    $type: "LayoutArea",
    id: "/",
    control: {
        $type: "LayoutStackControl",
        skin: "MainWindow",
        areas: [
            {
                $type: "LayoutArea",
                id: "/Main",
                control: {
                    $type: "MenuItemControl",
                    title: "Hello world",
                    icon: "systemorph-fill"
                }
            },
            {
                $type: "LayoutArea",
                id: "/Toolbar",
                control: {
                    $type: "InputBoxControl",
                    dataContext: {
                        value: "Hello world",
                        // ref: new WorkspaceReference(), // always reference to the main store
                    },
                    value: makeBinding("$.value") // json path
                }
            }
        ]
    }
}


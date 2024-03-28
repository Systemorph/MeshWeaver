import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import {
    DataChangedEvent,
    EntireWorkspace, JsonPatch, JsonPathReference,
    LayoutAreaReference, PatchOperation,
    SubscribeDataRequest
} from "@open-smc/data/src/data.contract";
import { messageOfType } from "@open-smc/message-hub/src/operators/messageOfType";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { Patch, produceWithPatches } from "immer";

// this is mock

export const dataSyncHub =
    new SubjectHub((input, output) => {
        input
            .pipe(messageOfType(SubscribeDataRequest))
            .subscribe(({message}) => {
                const {id, workspaceReference} = message;

                if (workspaceReference instanceof EntireWorkspace) {
                    sendMessage(output, new DataChangedEvent(id, workspace));

                    setTimeout(() => {
                        const [nextState, patches] =
                            produceWithPatches(
                                workspace,
                                state => {
                                    state.user.name = "bar";
                                });
                        sendMessage(
                            output,
                            new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
                        );
                    }, 1000)
                }

                if (workspaceReference instanceof LayoutAreaReference) {
                    sendMessage(output, new DataChangedEvent(id, layoutState));
return;
                    setTimeout(() => {
                        const [nextState, patches] =
                            produceWithPatches(
                                layoutState,
                                state => {
                                    // state.control.areas[0].control.title = "Hi";
                                    // state.control.areas[1].control.data = "Hi";
                                    // state.control.areas[0].id = "/ContextMenu";
                                    // state.control.areas.pop();
                                    // state.style = { fontWeight: "bold" }
                                    // state.id = "/root"
                                }
                            );

                        sendMessage(
                            output,
                            new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
                        )
                    }, 1000);
                }
            });
    });

const workspace = {
    user: {
        name: "foo"
    }
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

const layoutState = {
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
                        user: {
                            $type: "OpenSmc.Data.JsonPathReference",
                            path: "$.user"
                        }
                    },
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$.user.name"
                    }
                }
            },
            {
                $type: "OpenSmc.Layout.LayoutArea",
                id: "/ContextMenu",
                control: {
                    $type: "OpenSmc.Layout.TextBoxControl",
                    dataContext: {
                        $type: "OpenSmc.Data.JsonPathReference",
                        path: "$.user"
                    },
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$.name"
                    }
                }
            }
        ]
    }
}

function toPatchOperation(patch: Patch): PatchOperation {
    const {op, path, value} = patch;

    return {
        op,
        path: "/" + path.join("/"),
        value
    }
}


import { SubjectHub } from "@open-smc/message-hub/src/SubjectHub";
import {
    DataChangedEvent,
    EntireWorkspace, JsonPatch,
    LayoutAreaReference, PatchOperation,
    SubscribeRequest
} from "@open-smc/data/src/data.contract";
import { messageOfType } from "@open-smc/message-hub/src/operators/messageOfType";
import { sendMessage } from "@open-smc/message-hub/src/sendMessage";
import { Patch, produceWithPatches } from "immer";

export const backendHub =
    new SubjectHub((input, outgoing) => {
        input
            .pipe(messageOfType(SubscribeRequest))
            .subscribe(({message}) => {
                const {id, workspaceReference} = message;

                if (workspaceReference instanceof EntireWorkspace) {
                    sendMessage(outgoing, new DataChangedEvent(id, workspace));

                    setTimeout(() => {
                        const [nextState, patches] =
                            produceWithPatches(
                                workspace,
                                state => {
                                    state.user.name = "bar";
                                });
                        sendMessage(
                            outgoing,
                            new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
                        );
                    }, 1000)
                }

                if (workspaceReference instanceof LayoutAreaReference) {
                    sendMessage(outgoing, new DataChangedEvent(id, layoutState));
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
                            outgoing,
                            new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
                        )
                    }, 1000);
                }
            });
    });

const workspace = {
    menu: "Hello world",
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

const layout = {
    $type: "OpenSmc.Layout.LayoutAreaCollection",
    reference: {
        $type: "OpenSmc.Layout.LayoutAreaReference",
        area: "Main"
    },
    areas: {
        Main: {
            $type: "OpenSmc.Layout.Composition.LayoutStackControl",
            areas: [
                {
                    $type: "OpenSmc.Layout.LayoutAreaReference",
                    area: "Main/View1"
                },
                {
                    $type: "OpenSmc.Layout.LayoutAreaReference",
                    area: "Main/View2"
                }
            ]
        },
        "Main/View1": {
            $type: "OpenSmc.Layout.Composition.SpinnerControl",
            message: "processing...",
            progress: 0.5
        },
        "Main/View2": {
            $type: "OpenSmc.Layout.Composition.TextBoxControl",
            dataContext: {
                $type: "EntityReference",
                collection: "LineOfBusiness",
                id: "myLob"
            },
            data: {
                $type: "Binding",
                path: "$.name"
            }
        }
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
                    dataContext: {
                        $type: "OpenSmc.Data.JsonPathReference",
                        path: "$.menu"
                    },
                    title: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$"
                    },
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
                        path: "$.menu"
                    },
                    data: {
                        $type: "OpenSmc.Layout.DataBinding.Binding",
                        path: "$"
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


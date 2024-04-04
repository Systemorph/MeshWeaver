import { messageOfType } from "@open-smc/messaging/src/operators/messageOfType";
import { sendMessage } from "@open-smc/messaging/src/sendMessage";
import { produceWithPatches } from "immer";
import { SubscribeRequest } from "@open-smc/data/src/contract/SubscribeRequest";
import { EntireWorkspace } from "@open-smc/data/src/contract/EntireWorkspace";
import { DataChangedEvent } from "@open-smc/data/src/contract/DataChangedEvent";
import { JsonPatch } from "@open-smc/data/src/contract/JsonPatch";
import { LayoutAreaReference } from "@open-smc/data/src/contract/LayoutAreaReference";
import { filter, Observable, Observer, Subject } from "rxjs";
import { PatchChangeRequest } from "@open-smc/data/src/contract/PatchChangeRequest";
import { MessageDelivery } from "@open-smc/messaging/src/api/MessageDelivery";
import { workspace } from "./workspace";
import { layoutState } from "./layoutState";
import { toPatchOperation } from "./toPatchOperation";

export class SampleApp extends Observable<MessageDelivery> implements Observer<MessageDelivery> {
    protected input = new Subject<MessageDelivery>();
    protected output = new Subject<MessageDelivery>();

    constructor() {
        super(subscriber => this.output.subscribe(subscriber));

        this.input
            .pipe(filter(messageOfType(SubscribeRequest)))
            .subscribe(({message}) => this.subscribeRequestHandler(message));

        this.input
            .pipe(filter(messageOfType(PatchChangeRequest)))
            .subscribe(({message}) => this.patchChangeRequestHandler(message));
    }

    complete() {
    }

    error(err: any) {
    }

    next(value: MessageDelivery) {
        this.input.next(value);
    }

    subscribeRequestHandler(message: SubscribeRequest) {
        const {id, workspaceReference} = message;

        if (workspaceReference instanceof EntireWorkspace) {
            sendMessage(this.output, new DataChangedEvent(id, workspace));

            setTimeout(() => {
                const [nextState, patches] =
                    produceWithPatches(
                        workspace,
                        state => {
                            state.user.name = "bar";
                        });
                sendMessage(
                    this.output,
                    new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
                );
            }, 1000)
        }

        if (workspaceReference instanceof LayoutAreaReference) {
            sendMessage(this.output, new DataChangedEvent(id, layoutState));

            // setTimeout(() => {
            //     const [nextState, patches] =
            //         produceWithPatches(
            //             layoutState,
            //             state => {
            //                 // state.control.areas[0].control.title = "Hi";
            //                 // state.control.areas[1].control.data = "Hi";
            //                 // state.control.areas[0].id = "/ContextMenu";
            //                 // state.control.areas.pop();
            //                 // state.style = { fontWeight: "bold" }
            //                 // state.id = "/root"
            //             }
            //         );
            //
            //     sendMessage(
            //         this.output,
            //         new DataChangedEvent(id, new JsonPatch(patches.map(toPatchOperation)))
            //     )
            // }, 1000);
        }
    }

    patchChangeRequestHandler(message: PatchChangeRequest) {
        console.log(message);
    }
}

export const sampleApp = new SampleApp();
// import { PresenterSpec } from "@open-smc/application/renderPresenter";
// import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { DisposeSessionDialogEvent, ShowSessionDialogEvent } from "./notebookEditor/notebookEditor.contract";
import { ControlDef } from "@open-smc/application/ControlDef";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";

export function useSubscribeToSessionDialogEvent(handler: (sessionDialog?: ControlDef) => void) {
    const {receiveMessage} = useMessageHub();

    // const unsubscribeShow = receiveMessage(
    //     ShowSessionDialogEvent,
    //     (evt) => handler(evt.presenter),
    //     // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
    //     x => true)
    //
    // const unsubscribeDispose = receiveMessage(
    //     DisposeSessionDialogEvent,
    //     (evt) => handler(null),
    //     // TODO: eventId should be introduced, otherwise this doesn't guarantee the uniqueness of event (8/16/2022, akravets)
    //     x => true)
    //
    // return () => {
    //     unsubscribeShow();
    //     unsubscribeDispose();
    // }
}
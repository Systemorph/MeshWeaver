import { useNotebookEditorStore } from "../../NotebookEditor";
import { useMessageHub } from "@open-smc/application/src/messageHub/AddHub";
import { SessionStatusEvent } from "../../notebookEditor/notebookEditor.contract";
import { useEffect } from "react";

export function useSubscribeToSessionStatusEvent() {
    const {receiveMessage} = useMessageHub();
    const {setState} = useNotebookEditorStore();

    useEffect(() => {
        return receiveMessage(SessionStatusEvent, ({session}) => {
            setState(state => {
                state.session = session;
            })
        });
    }, [receiveMessage, setState]);
}
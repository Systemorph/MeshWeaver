import { useNotebookEditorStore } from "../../NotebookEditor";
import { useEffect } from "react";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { SessionEvaluationStatusChangedEvent } from "../../notebookEditor/notebookEditor.contract";

export function useSubscribeToSessionEvaluationStatusChanged() {
    const {setState} = useNotebookEditorStore();
    const {receiveMessage} = useMessageHub();

    useEffect(() => {
        return receiveMessage(SessionEvaluationStatusChangedEvent,({evaluationStatus}) => {
            setState(state => {
                state.evaluationStatus = evaluationStatus;
            })
        });
    }, [setState, receiveMessage]);
}
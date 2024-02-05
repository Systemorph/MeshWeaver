import { useElementsStore } from "../../NotebookEditor";
import { useEffect } from "react";
import { useMessageHub } from "@open-smc/application/src/messageHub/AddHub";
import {
    NotebookElementEvaluationStatusEvent
} from "../../notebookEditor/notebookEditor.contract";

export function useSubscribeToElementStatusChanged() {
    const {setState} = useElementsStore();
    const {receiveMessage} = useMessageHub();

    useEffect(() => {
        return receiveMessage(
            NotebookElementEvaluationStatusEvent,
            (event) => {
                const {elementId, evaluationStatus, evaluationCount, evaluationTime, evaluationError} = event;
                setState(models => {
                    const {element} = models[elementId];
                    if (element) {
                        element.evaluationStatus = evaluationStatus;
                        element.evaluationCount = evaluationCount;
                        element.evaluationTime = evaluationTime;
                        element.evaluationError = evaluationError;
                    }
                })
            }
        );
    }, []);
}
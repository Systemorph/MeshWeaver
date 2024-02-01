import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { intersection } from "lodash";
import { useChangeElementContent } from "./useChangeElementContent";
import { useToast } from "@open-smc/application/notifications/useToast";
import { EvaluationStatus } from "../../../../app/notebookFormat";
import { EvaluateElementsCommand } from "../../notebookEditor/notebookEditor.contract";
import { validateStatus } from "@open-smc/application/messageHub/validateStatus";
import { useMessageHubExtensions } from "@open-smc/application/messageHub/useMessageHubExtensions";

export function useEvaluateElements() {
    const {showToast} = useToast();
    const {getState, setState} = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const {flushElementContentChanges} = useChangeElementContent();
    const {sendMessageAndWaitForResponse} = useMessageHubExtensions("NotebookEditorControl");

    return async (elementIdsToEvaluate: string[]) => {
        const {elementIds, session} = getState();
        const {status: sessionStatus} = session;

        flushElementContentChanges();

        const orderedElementIdsToEvaluate = intersection(elementIds, elementIdsToEvaluate);

        const implicitSessionStart = sessionStatus !== 'Running' && sessionStatus !== 'Starting';

        if (implicitSessionStart) {
            setState(state => {
                state.session.status = "Starting";
            });
        }

        function setStatus(elementIds: string[], status: EvaluationStatus) {
            elementsStore.setState(models => {
                for (const elementId of elementIds) {
                    models[elementId].element.evaluationStatus = status;
                }
            })
        }

        setState(state => {
            state.evaluationStatus = 'Pending';
        });

        setStatus(orderedElementIdsToEvaluate, 'Pending');

        try {
            const event = new EvaluateElementsCommand(orderedElementIdsToEvaluate);

            const {status} = await sendMessageAndWaitForResponse(
                event,
                null,
                EvaluateElementsCommand,
                ({message: {eventId}}) => event.eventId === eventId
            );

            validateStatus(status);

        } catch (error) {
            showToast('Error', `Failed to start evaluation. ${error}.`, 'Error');

            setStatus(orderedElementIdsToEvaluate, 'Idle');

            setState(state => {
                state.evaluationStatus = 'Idle';
            });

            if (implicitSessionStart) {
                setState(state => {
                    state.session = session;
                });
            }

            throw error;
        }
    }
}


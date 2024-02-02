import { useNotebookEditorStore } from "../../NotebookEditor";
import { SessionSpecification } from "../../../../app/notebookFormat";
import { useToast } from "@open-smc/application/src/notifications/useToast";
import { StartSessionEvent } from "../../notebookEditor/notebookEditor.contract";
import { validateStatus } from "@open-smc/application/src/messageHub/validateStatus";
import { useMessageHubExtensions } from "@open-smc/application/src/messageHub/useMessageHubExtensions";

export function useStartSession() {
    const {showToast} = useToast();
    const {getState, setState} = useNotebookEditorStore();
    const {sendMessageAndWaitForResponse} = useMessageHubExtensions();

    return async (specification: SessionSpecification) => {
        const {session} = getState();

        setState(state => {
            state.session.status = "Starting";
        });

        try {
            const event = new StartSessionEvent(specification);

            const {status} = await sendMessageAndWaitForResponse(
                event,
                null,
                StartSessionEvent,
                ({message: {eventId}}) => event.eventId === eventId
            );

            validateStatus(status);
        }
        catch (error) {
            showToast('Error', `Failed to start session. ${error}.`, 'Error');

            setState(state => {
                state.session = session;
            });

            throw error;
        }
    }
}
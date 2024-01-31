import { useNotebookEditorStore } from "../../NotebookEditor";
import { useToast } from "@open-smc/application/useToast";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { StopSessionEvent } from "../../notebookEditor/notebookEditor.contract";

export function useStopSession() {
    const {showToast} = useToast();
    const {getState, setState} = useNotebookEditorStore();
    const {sendMessage} = useMessageHub();

    return async () => {
        const {session} = getState();

        setState(state => {
            state.session.status = "Stopping";
        });

        sendMessage(new StopSessionEvent());

        // try {
        //     await sendMessage(new StopSessionEvent());
        // }
        // catch (error) {
        //     showToast('Error', `Failed to stop session. ${error}`);
        //
        //     setState(state => {
        //         state.session = session;
        //     });
        //
        //     throw error;
        // }
    }
}
import { useToast } from "@open-smc/application/src/notifications/useToast";
import { useMessageHub } from "@open-smc/application/src/messageHub/AddHub";
import { CancelCommand } from "../../notebookEditor/notebookEditor.contract";

export function useStopEvaluation() {
    const {showToast} = useToast();
    const {sendMessage} = useMessageHub();

    return async () => {
        sendMessage(new CancelCommand());
    }
}
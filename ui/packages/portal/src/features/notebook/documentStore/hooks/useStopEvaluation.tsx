import { useToast } from "@open-smc/application/useToast";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { CancelCommand } from "../../notebookEditor/notebookEditor.contract";

export function useStopEvaluation() {
    const {showToast} = useToast();
    const {sendMessage} = useMessageHub();

    return async () => {
        sendMessage(new CancelCommand());
    }
}
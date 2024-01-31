import { useDeleteElements } from "./useDeleteElements";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { NotebookElementDeletedEvent } from "../../notebookEditor/notebookEditor.contract";

export function useDeleteElementsAction() {
    const deleteElements = useDeleteElements();
    const {sendMessage} = useMessageHub();

    return async (elementIdsToDelete: string[]) => {
        deleteElements(elementIdsToDelete);
        sendMessage(new NotebookElementDeletedEvent(elementIdsToDelete));
    }
}
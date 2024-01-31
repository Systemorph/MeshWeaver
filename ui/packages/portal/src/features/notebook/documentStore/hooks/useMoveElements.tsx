import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { moveElements } from "../moveElements";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { NotebookElementMovedEvent } from "../../notebookEditor/notebookEditor.contract";

export function useMoveElements() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const updateMarkdown = useUpdateMarkdown();
    const {sendMessage} = useMessageHub();

    return async (elementIds: string[], afterElementId: string) => {
        notebookEditorStore.setState(state => {
            state.elementIds = moveElements(state.elementIds, elementIds, afterElementId);
        });

        const models = elementsStore.getState();

        if (elementIds.some(elementId => models[elementId].element.elementKind === 'markdown')) {
            updateMarkdown();
        }

        sendMessage(new NotebookElementMovedEvent(elementIds, afterElementId));
    }
}
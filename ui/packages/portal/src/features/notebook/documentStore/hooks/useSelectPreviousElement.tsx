import { FocusMode } from "../../ElementEditor";
import { useNotebookEditorStore } from "../../NotebookEditor";
import { useSelectElement } from "./useSelectElement";

export function useSelectPreviousElement() {
    const {getState} = useNotebookEditorStore();
    const selectElement = useSelectElement();

    const selectPreviousElement = (elementId: string, scrollIntoView?: boolean, focusMode?: FocusMode) => {
        const {elementIds} = getState();
        const index = elementIds.indexOf(elementId);

        if (index !== 0) {
            const previousElementId = elementIds[index - 1];
            selectElement(previousElementId, null, null, scrollIntoView, focusMode);
        }
    }

    return selectPreviousElement;
}


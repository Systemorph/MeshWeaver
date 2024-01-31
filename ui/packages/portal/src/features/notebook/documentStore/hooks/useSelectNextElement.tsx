import { FocusMode } from "../../ElementEditor";
import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { useCreateElement } from "./useCreateElement";
import { useSelectElement } from "./useSelectElement";

export function useSelectNextElement() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const createElement = useCreateElement();
    const selectElement = useSelectElement();

    const selectNextElement = (elementId: string, scrollIntoView?: boolean, focusMode?: FocusMode, createNextElement?: boolean) => {
        const {elementIds} = notebookEditorStore.getState();
        const models = elementsStore.getState();
        const index = elementIds.indexOf(elementId);

        if (index !== elementIds.length - 1) {
            const nextElementId = elementIds[index + 1];
            selectElement(nextElementId, null, null, scrollIntoView, focusMode);
        }

        if (index === elementIds.length - 1 && createNextElement) {
            return createElement(models[elementId].element.elementKind, elementId);
        }
    }

    return selectNextElement;
}


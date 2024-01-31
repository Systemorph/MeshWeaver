import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { without } from "lodash";

export function useDeleteElements() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const updateMarkdown = useUpdateMarkdown();

    return (elementIds: string[]) => {
        notebookEditorStore.setState(state => {
            state.elementIds = without(state.elementIds, ...elementIds);
            state.selectedElementIds = without(state.selectedElementIds, ...elementIds);

            if (state.selectedElementIds.indexOf(state.activeElementId) === -1) {
                state.activeElementId = null;
            }
        });

        const models = elementsStore.getState();

        elementsStore.setState(elements => {
            for (const elementId of elementIds) {
                delete elements[elementId];
            }
        });

        if (elementIds.some(elementId => models[elementId].element.elementKind === 'markdown')) {
            updateMarkdown();
        }
    }
}
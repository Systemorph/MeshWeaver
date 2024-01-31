import { useElementsStore, useNotebookEditorStore } from "../../NotebookEditor";
import { pull, union } from "lodash";
import { isElementInViewport } from "../../../../shared/utils/helpers";
import { FocusMode } from "../../ElementEditor";

export function useSelectElement() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();

    const selectElement = (elementId: string, ctrlKeyPressed?: boolean, shiftKeyPressed?: boolean, scrollIntoView?: boolean, focusMode?: FocusMode) => {
        const {elementIds, selectedElementIds, activeElementId} = notebookEditorStore.getState();
        const models = elementsStore.getState();
        const {ref: elementRef, editorRef} = models[elementId];

        let newSelectedElementIds: string[];

        if (ctrlKeyPressed) {
            newSelectedElementIds = [...selectedElementIds];
            if (!newSelectedElementIds.includes(elementId)) {
                newSelectedElementIds.push(elementId);
            } else {
                pull(newSelectedElementIds, elementId);
            }
        } else if (shiftKeyPressed) {
            const firstSelectedElementIndex = elementIds.indexOf(
                activeElementId
            );
            const lastSelectedElementIndex = elementIds.indexOf(
                elementId
            );
            if (firstSelectedElementIndex === -1) {
                newSelectedElementIds = [elementId];
            } else {
                let diffArray: string[];
                if (lastSelectedElementIndex >= firstSelectedElementIndex) {
                    diffArray = elementIds.slice(firstSelectedElementIndex, lastSelectedElementIndex + 1);
                    newSelectedElementIds = union(selectedElementIds, diffArray);
                } else if (lastSelectedElementIndex < firstSelectedElementIndex) {
                    diffArray = elementIds.slice(lastSelectedElementIndex, firstSelectedElementIndex + 1);
                    newSelectedElementIds = union(selectedElementIds, diffArray);
                }
            }
        } else {
            newSelectedElementIds = [elementId];
        }

        if (elementIds.indexOf(activeElementId) !== -1 && elementId !== activeElementId && models[activeElementId].element.elementKind === 'markdown') {
            elementsStore.setState(elements => {
                elements[activeElementId].isEditMode = false;
            });
        }

        if (focusMode) {
            elementsStore.setState(elements => {
                elements[elementId].isEditMode = true;
            });

            editorRef.current?.focus(focusMode);
        }

        notebookEditorStore.setState(state => {
            state.selectedElementIds = newSelectedElementIds;
            state.activeElementId = elementId;
        })

        if (scrollIntoView && elementRef.current && !isElementInViewport(elementRef.current)) {
            elementRef.current.scrollIntoView();
        }
    }

    return selectElement;
}


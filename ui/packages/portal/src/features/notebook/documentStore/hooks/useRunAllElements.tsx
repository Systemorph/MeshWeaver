import { useElementsStore, useNotebookEditorSelector } from "../../NotebookEditor";
import { useEvaluateElements } from "./useEvaluateElements";

export function useRunAllElements() {
    const elementIds = useNotebookEditorSelector('elementIds');
    const elementsStore = useElementsStore();
    const evaluate = useEvaluateElements();

    return () => {
        const elements = elementsStore.getState();

        const codeElementIds = elementIds.filter(elementId => elements[elementId].element.elementKind === 'code');
        const markdownElementIds = elementIds.filter(elementId => elements[elementId].element.elementKind === 'markdown');

        if (markdownElementIds.length) {
            elementsStore.setState(models => {
                for (const elementId of markdownElementIds) {
                    models[elementId].isEditMode = false;
                }
            });
        }

        if (codeElementIds.length) {
            evaluate(codeElementIds);
        }
    }
}

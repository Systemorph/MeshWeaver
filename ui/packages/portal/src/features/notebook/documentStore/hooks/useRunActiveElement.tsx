import { useEvaluateElements } from "./useEvaluateElements";
import { useActiveElement } from "./useActiveElement";
import { useElementsStore } from "../../NotebookEditor";

export function useRunActiveElement() {
    const activeElement = useActiveElement();
    const canRunActiveElement =
        activeElement?.element?.elementKind === 'code' && activeElement?.element?.evaluationStatus === 'Idle'
        || activeElement?.element?.elementKind === 'markdown';
    const evaluate = useEvaluateElements();
    const elementsStore = useElementsStore();

    const runActiveElement = async () => {
        const {element: {elementKind}} = activeElement;

        if (elementKind === 'code') {
            return evaluate([activeElement.elementId]);
        } else {
            return elementsStore.setState(models => {
                models[activeElement.elementId].isEditMode = false;
            });
        }
    }

    return {runActiveElement, canRunActiveElement};
}
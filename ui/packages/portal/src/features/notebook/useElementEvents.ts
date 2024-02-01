import { ElementKind } from "../../app/notebookFormat";
import { useSelectNextElement } from "./documentStore/hooks/useSelectNextElement";
import { useSelectPreviousElement } from "./documentStore/hooks/useSelectPreviousElement";
import { useEvaluateElements } from "./documentStore/hooks/useEvaluateElements";
import { RefObject, useEffect } from "react";
import { useEventListener } from "usehooks-ts";
import { useSelectElement } from "./documentStore/hooks/useSelectElement";
import { NotebookPermissions } from "./documentStore/documentState";
import { useElementsStore } from "./NotebookEditor";

export function useElementEvents(elementId: string,
                                 elementKind: ElementKind,
                                 elementRef: RefObject<HTMLDivElement>,
                                 enableKeyboardEvents: boolean,
                                 permissions: NotebookPermissions) {
    const {canEdit, canRun} = permissions;
    const selectElement = useSelectElement();
    const selectNextElement = useSelectNextElement();
    const selectPreviousElement = useSelectPreviousElement();
    const evaluateElements = useEvaluateElements();
    const elementsStore = useElementsStore();

    useEventListener(
        'mousedown',
        event => {
            if (event.shiftKey) {
                event.preventDefault();
            }
            selectElement(elementId, event.ctrlKey, event.shiftKey);
        },
        elementRef);

    useEffect(() => {
        if (enableKeyboardEvents) {
            document.addEventListener('keydown', handleKeyboardEvent);
            return () => document.removeEventListener('keydown', handleKeyboardEvent);
        }
    }, [enableKeyboardEvents]);

    function handleKeyboardEvent(event: KeyboardEvent) {
        if (event.code === 'ArrowDown') {
            selectNextElement(elementId, true);
        } else if (event.code === 'ArrowUp') {
            selectPreviousElement(elementId, true);
        } else if (event.code === 'Enter') {
            if ((event.shiftKey || event.altKey || event.ctrlKey)) {
                if (canRun && elementKind === 'code') {
                    evaluateElements([elementId]);
                    (event.altKey || event.shiftKey) && selectNextElement(elementId, true, null, true);
                    event.preventDefault();
                    event.stopPropagation();
                }
                if (elementKind === 'markdown') {
                    (event.shiftKey || event.altKey) && selectNextElement(elementId, true, null, true);
                    event.preventDefault();
                    event.stopPropagation();
                    elementsStore.setState(models => {
                        models[elementId].isEditMode = false;
                    });
                }
            } else {
                if (canEdit && elementKind === 'markdown') {
                    elementsStore.setState(models => {
                        models[elementId].isEditMode = true;
                    });
                    event.preventDefault();
                    event.stopPropagation();
                }
            }
        }
    }
}
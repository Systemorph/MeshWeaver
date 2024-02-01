import { useElementsStore, useNotebookEditorSelector, useNotebookEditorStore } from "../../NotebookEditor";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { isEmpty } from "lodash";
import { useCallback } from "react";
import { NotebookElementChangeData, NotebookElementContentChangedEvent } from "../../notebookElement.contract";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";

export function useChangeElementContent() {
    const {setState} = useNotebookEditorStore();
    const updateMarkdown = useUpdateMarkdown();
    const {debouncedFunc} = useNotebookEditorSelector("elementChanges");
    const elementsStore = useElementsStore();
    const {sendMessage} = useMessageHub();

    const changeElementContent = useCallback((elementId: string, changes: NotebookElementChangeData[], content: string) => {
        const {elementChanges: {buffer}} = setState(({elementChanges}) => {
            if (elementId !== elementChanges.elementId && !isEmpty(elementChanges.buffer)) {
                throw `Got changes for new elementId while buffer is not empty`;
            }

            elementChanges.elementId = elementId;
            elementChanges.buffer.push(...changes);
        });

        debouncedFunc(() => {
            sendMessage(new NotebookElementContentChangedEvent(elementId, buffer));

            setState(({elementChanges}) => {
                elementChanges.buffer = [];
            });
        });

        const {[elementId]: {element: {elementKind}}} = elementsStore.setState(({[elementId]: {element}}) => {
            element.content = content;
        });

        if (elementKind === 'markdown') {
            updateMarkdown([elementId]);
        }
    }, []);

    const flushElementContentChanges = debouncedFunc.flush;

    return {changeElementContent, flushElementContentChanges};
}


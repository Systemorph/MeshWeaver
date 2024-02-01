import { useEffect } from "react";
import { NotebookElementMovedEvent } from "../../notebookEditor/notebookEditor.contract";
import { debounce, flatten, map } from "lodash";
import { useElementsStore, useNotebookEditorSelector, useNotebookEditorStore } from "../../NotebookEditor";
import { useUpdateMarkdown } from "./useUpdateMarkdown";
import { moveElements } from "../moveElements";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";

export function useSubscribeToMovedElements() {
    const notebook = useNotebookEditorSelector("notebook");
    const {receiveMessage} = useMessageHub();
    const flush = useFlush();

    useEffect(() => {
        let buffer: NotebookElementMovedEvent[] = [];

        const moveElementsDebounced = debounce(() => {
            flush(buffer);
            buffer = [];
        }, 100);

        return receiveMessage(
            NotebookElementMovedEvent,
            event => {
                buffer.push(event);
                moveElementsDebounced();
            },
            ({message}) => message.notebookId === notebook.id
        );
    }, [receiveMessage, notebook]);
}

function useFlush() {
    const notebookEditorStore = useNotebookEditorStore();
    const elementsStore = useElementsStore();
    const updateMarkdown = useUpdateMarkdown();

    return (events: NotebookElementMovedEvent[]) => {
        notebookEditorStore.setState(state => {
            for (const {elementIds, afterElementId} of events) {
                state.elementIds = moveElements(state.elementIds, elementIds, afterElementId);
            }
        });

        const elementIds = flatten(map(events, "elementIds"));
        const models = elementsStore.getState();

        if (elementIds.some(elementId => models[elementId].element.elementKind === 'markdown')) {
            updateMarkdown();
        }
    }
}
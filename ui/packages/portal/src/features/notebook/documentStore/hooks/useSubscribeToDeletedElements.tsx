import { useEffect } from "react";
import { debounce } from "lodash";
import { useDeleteElements } from "./useDeleteElements";
import { useNotebookEditorSelector } from "../../NotebookEditor";
import { useMessageHub } from "@open-smc/application/messageHub/AddHub";
import { NotebookElementDeletedEvent } from "../../notebookEditor/notebookEditor.contract";

export function useSubscribeToDeletedElements() {
    const notebook = useNotebookEditorSelector("notebook");
    const {receiveMessage} = useMessageHub();
    const deleteElements = useDeleteElements();

    useEffect(() => {
        let buffer: string[] = [];

        const deleteElementsDebounced = debounce(() => {
            deleteElements(buffer);
            buffer = [];
        }, 100);

        return receiveMessage(
            NotebookElementDeletedEvent,
            event => {
                buffer.push(...event.elementIds);
                deleteElementsDebounced();
            },
            ({message}) => message.notebookId === notebook.id
        );
    }, [receiveMessage, notebook, deleteElements]);
}
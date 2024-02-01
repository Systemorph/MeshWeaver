import { useFileExplorerSelector, useFileExplorerStore, useFileStore } from "../../ProjectExplorerContextProvider";
import { FolderAddedEvent, NotebookAddedEvent } from "../../projectExplorerEditorGrain/projectExplorerEditor.contract";
import { useMessageHubExtensions } from "@open-smc/application/messageHub/useMessageHubExtensions";

export function useSaveNewFile() {
    const fileStore = useFileStore();
    const path = useFileExplorerSelector("path");
    const {makeRequest} = useMessageHubExtensions();

    return async (fileId: string) => {
        const {[fileId]: {name, kind}} = fileStore.getState();

        if (kind !== 'Folder' && kind !== 'Notebook') {
            throw `Folder or notebook node is expected, got ${kind}`;
        }

        const eventType = kind === 'Folder' ? FolderAddedEvent : NotebookAddedEvent;

        const {id} = await makeRequest(new eventType(name, path), eventType);

        fileStore.setState(files => {
            if (files[fileId]) {
                files[fileId].actualFileId = id;
            }
        });
    }
}
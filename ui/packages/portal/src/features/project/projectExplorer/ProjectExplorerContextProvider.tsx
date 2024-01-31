import { compareFiles, FileModel, FileExplorerState } from "./projectExplorerStore/fileExplorerState";
import { createStore, Store } from "@open-smc/store/store";
import { createContext, PropsWithChildren, useContext, useMemo } from "react";
import { useProject } from "../projectStore/hooks/useProject";
import { useEnv } from "../projectStore/hooks/useEnv";
import { ProjectApi } from "../../../app/projectApi";
import { EnvAccessControlApi } from "../envSettings/accessControl/envAccessControlApi";
import { keyBy, map } from "lodash";
import { getUseSelector } from "@open-smc/store/useSelector";

interface ProjectExplorerContext {
    store: Store<FileExplorerState>;
    fileStore: Store<Record<string, FileModel>>;
}

const context = createContext<ProjectExplorerContext>(null);

export function useProjectExplorerContext() {
    return useContext(context);
}

export function useFileExplorerStore() {
    const {store} = useProjectExplorerContext();
    return store;
}

export function useFileStore() {
    const {fileStore} = useProjectExplorerContext();
    return fileStore;
}

export const useFileExplorerSelector = getUseSelector(useFileExplorerStore);

export const useFileStoreSelector = getUseSelector(useFileStore);

interface ProjectExplorerContextProviderProps {
    files: FileModel[];
    path: string;
}

export function ProjectExplorerContextProvider({files, path, children}: PropsWithChildren<ProjectExplorerContextProviderProps>) {
    const {project} = useProject();
    const {envId} = useEnv();

    const contextValue = useMemo(() => {
        files = [...files];
        files.sort(compareFiles);

        const fileIds = map(files, "id");

        const fileStore = createStore(keyBy(files, "id"));

        const store = createStore<FileExplorerState>({
            fileIds,
            path
        });

        async function onPathChange() {
            const {path} = store.getState();
            const node = await ProjectApi.getNode(project.id, envId, path);
            const canEdit = await EnvAccessControlApi.getPermission(project.id, 'Edit', envId, node.id);

            store.setState(state => {
                state.canEdit = canEdit;
            });
        }

        store.subscribe("path", onPathChange);

        void onPathChange();

        return {
                store,
                fileStore
            }
        },
        [files, path]
    );

    return (
        <context.Provider
            value={contextValue}
            children={children}
        />
    );
}
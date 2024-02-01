import { createContext } from "react";
import { getStoreHooks, getStoreProvider, Store } from "@open-smc/store/store";
// import {
//     getProjectExplorerEditorViewModel
// } from "../projectExplorer/projectExplorerEditorGrain/projectExplorerEditorGrain";
import { EnvAccessControlApi } from "./accessControl/envAccessControlApi";
import { Env, ProjectApi, ProjectNode } from "../../../app/projectApi";

export type EnvSettingsState = {
    readonly envId: string;
    readonly node?: ProjectNode;
    readonly viewModelId: string;
    readonly permissions: EnvPermissions;
}

export type EnvPermissions = {
    canEdit: boolean;
    isOwner: boolean;
}

const context = createContext<Store<EnvSettingsState>>(null)

export const {useStore, useSelector} = getStoreHooks(context);
export const EnvSettingsStore = getStoreProvider(context);

export async function getInitialState(projectId: string, envId: string, nodeId?: string) {
    let env: Env;
    let node: ProjectNode;

    if (nodeId) {
        node = await ProjectApi.getNodeById(projectId, envId, nodeId);
    } else {
        env = await ProjectApi.getEnv(projectId, envId);
    }

    // const viewModelId = await getProjectExplorerEditorViewModel(projectId, envId);
    const permissions = await getEnvPermissions(projectId, envId, nodeId);

    return {
        envId,
        node,
        viewModelId: "",
        permissions,
    }
}

export async function getEnvPermissions(projectId: string, envId: string, nodeId: string) {
    const canEdit = await EnvAccessControlApi.getPermission(projectId, 'Edit', envId, nodeId);
    const isOwner = await EnvAccessControlApi.getPermission(projectId, 'Owner', envId, nodeId);

    return {
        canEdit,
        isOwner
    }
}


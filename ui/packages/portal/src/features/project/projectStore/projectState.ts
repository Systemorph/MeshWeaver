import { Env, Project, ProjectApi, ProjectNode } from "../../../app/projectApi";
import { ProjectAccessControlApi } from "../projectSettings/accessControl/projectAccessControlApi";
import { OpenProjectEvent } from "../projectEditor.contract";

export type ProjectState = {
    readonly viewModelId?: string;
    readonly project: Project;
    readonly currentEnv: CurrentEnv;
    readonly activeFile?: ProjectNode;
    readonly permissions: ProjectPermissions;
}

export type CurrentEnv = {
    readonly envId: string;
    readonly isLoading?: boolean;
    readonly env?: Env;
    readonly error?: unknown;
}

export type ProjectPermissions = {
    canEdit: boolean;
    isOwner: boolean;
}

export async function getInitialState(projectId: string, envId: string): Promise<ProjectState> {
    const project = await ProjectApi.getProject(projectId);
    const permissions = await getProjectPermissions(projectId);
    // TODO: should be done lazily, e.g. once AC editing is needed (2/21/2023, akravets)
    // const viewModelId = await getProjectEditorViewModel(projectId);

    envId = envId || project.defaultEnvironment;

    const {env, error} = await loadEnv(projectId, envId);

    const currentEnv = {envId, env, error};

    return {
        project,
        permissions,
        // viewModelId,
        currentEnv
    };
}

export async function loadEnv(projectId: string, envId: string) {
    try {
        const env = await ProjectApi.getEnv(projectId, envId);
        return {env};
    } catch (error) {
        return {error};
    }
}

export async function getProjectPermissions(projectId: string) {
    const canEdit = await ProjectAccessControlApi.getPermission(projectId, 'Edit');
    const isOwner = await ProjectAccessControlApi.getPermission(projectId, 'Owner');

    return {
        canEdit,
        isOwner
    };
}

// export async function getProjectEditorViewModel(projectId: string) {
//     const {
//         viewModelId,
//         isNew
//     } = await ApplicationHub.getOrCreateViewModel('ProjectEditor', projectId);
//
//     if (isNew) {
//         await ApplicationHub.makeRequest(viewModelId, new OpenProjectEvent(projectId), OpenProjectEvent);
//     }
//
//     return viewModelId;
// }
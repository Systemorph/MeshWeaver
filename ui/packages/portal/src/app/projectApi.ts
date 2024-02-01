import axios, { AxiosError, CancelToken } from "axios";
import { NotebookDto } from "../controls/NotebookEditorControl";
import { NotebookElementDto } from "../controls/ElementEditorControl";

// TODO V10: Change this to CDN url and kill placeholder.jpg from assets (2022-11-02, Andrei Sirotenko)
export const defaultThumbnail = '/placeholder.jpg';

export type User = {
    id: string;
    name: string;
    affiliation: string;
}

export type Project = ProjectCatalogItem & {
    environments: string[];
    defaultEnvironment: string;
}

export type ProjectCatalogItem = {
    readonly id: string;
    readonly name: string;
    readonly thumbnail: string;
    readonly homeRegion: string;
    readonly createdOn: string;
    readonly isPublic: boolean;
    readonly tags: string[];
    readonly abstract: string;
    readonly authors: string[];
    readonly version: number;
}

export type ProjectSettings = Pick<Project, 'id' | 'name' | 'homeRegion' | 'abstract' | 'environments'| 'thumbnail'> & {
    environment: string;
    // todo see ticket #25295 
    defaultEnvironment?: string;
}

export type Region = {
    systemName: string;
    displayName: string;
    isDefault?: boolean
}

export type ProjectNodeKind = 'Folder' | 'Notebook' | 'Blob';

export type ProjectNode = {
    id: string;
    name: string;
    path: string;
    kind: ProjectNodeKind;
}

export type Env = {
    id: string;
    isCloning: boolean;
}

export type RecentTypeParam = '' | 'public' | 'all';

export type ProjectParams = {
    search?: string,
    page?: number,
    pageSize?: number,
    mode?: RecentTypeParam
};

export type ProjectTuple = {
    projects: ProjectCatalogItem[],
    total: number
};

export namespace ProjectApi {
    export async function getPublicProjects(params: ProjectParams): Promise<ProjectTuple> {
        const response = await axios.get<ProjectCatalogItem[]>(`/api/projects/public`, {params});
        return {
            projects: response.data,
            total: parseInt(response.headers['x-total-count']) || 0
        };
    }

    export async function getRecentProjects(params: ProjectParams): Promise<ProjectTuple> {
        const response = await axios.get<ProjectCatalogItem[]>(`/api/projects/recent`, {params});
        return {
            projects: response.data,
            total: parseInt(response.headers['x-total-count']) || 0
        };
    }

    export async function getProject(id: string) {
        try {
            const response = await axios.get<Project>(`/api/project/${id}`);
            return response.data;
        }
        catch (error) {
            if ((error as AxiosError)?.response) {
                throw (error as AxiosError)?.response?.status;
            }
        }
    }

    let counter = 5;

    export async function getEnv(projectId: string, envId: string) {
        try {
            const response = await axios.get<Env>(`/api/project/${projectId}/env/${envId}`);

            // if (envId.startsWith('dev')) {
            //     console.log(`get env ${envId}...${counter}`)
            //     const isCloning = counter-- > 0;
            //     if (!isCloning) counter = 5;
            //     return {id: envId, isCloning};
            // }
            //
            // if (envId.startsWith('notfound')) {
            //     throw {response: {status: 404}};
            // }
            //
            // if (envId.startsWith('forbidden')) {
            //     throw {response: {status: 403}};
            // }

            return response.data;
        } catch (error) {
            if ((error as AxiosError)?.response) {
                throw (error as AxiosError)?.response?.status;
            }
        }
    }

    export async function getProjectFiles(projectId: string, environmentId: string, parentPath: string) {
        const params = {parentPath};
        const response = await axios.get<ProjectNode[]>(
            `/api/project/${projectId}/env/${environmentId}/files`,
            {params}
        );

        return response.data;
    }

    export async function getNode(projectId: string, environmentId: string, path: string) {
        try {
            const params = {path};

            const response = await axios.get<ProjectNode>(
                `/api/project/${projectId}/env/${environmentId}/file`,
                {params},
            );

            return response.data;
        }
        catch (error) {
            if ((error as AxiosError)?.response) {
                throw (error as AxiosError)?.response?.status;
            }
        }
    }

    export async function getNodeById(projectId: string, envId: string, nodeId: string) {
        try {
            const params = {nodeId}
            const response = await axios.get<ProjectNode>(`/api/project/${projectId}/env/${envId}/node`, {params});
            return response.data;
        }
        catch (error) {
            if ((error as AxiosError)?.response) {
                throw (error as AxiosError)?.response?.status;
            }
        }
    }

    export async function getOverview(projectId: string, env: string) {
        const response = await axios.get<string>(`/api/project/${projectId}/env/${env}/overview`);
        return response.data;
    }

    export async function getNotebook(projectId: string, env: string, notebookId: string) {
        const response = await axios.get<NotebookDto>(`/api/project/${projectId}/env/${env}/notebook/${notebookId}`);
        return response.data;
    }

    export async function getNotebookElements(projectId: string,
                                              env: string,
                                              notebookId: string,
                                              page?: number,
                                              pageSize?: number) {
        const params = {page, pageSize};
        const response = await axios.get<NotebookElementDto[]>(
            `/api/project/${projectId}/env/${env}/notebook/${notebookId}/elements`,
            {params}
        );
        return response.data;
    }

    // export async function getElementOutput(projectId: string,
    //                                        env: string,
    //                                        notebookId: string,
    //                                        elementId: string,
    //                                        outputToken: string) {
    //     const response = await axios.get<PresenterSpec>(`/api/project/${projectId}/env/${env}/notebook/${notebookId}/element/${elementId}/output/${outputToken}`);
    //     return response.data;
    // }

    export async function getRegions() {
        const response = await axios.get<Region[]>(`/api/regions`);
        return response.data;
    }

    export async function suggestId(seed: string) {
        const response = await axios.post<string>(`/api/project/id/suggest/?name=${seed}`);
        return response.data + '';
    }

    export async function validateId(id: string) {
        const response = await axios.post<string[]>(`/api/project/id/validate/?id=${id}`);
        return response.data;
    }

    export async function createProject(projectSettings: ProjectSettings) {
        // todo see ticket #25295 
        projectSettings.defaultEnvironment = projectSettings.environment;
        const response = await axios.post<Project>(`/api/project`, projectSettings);
        return response.data;
    }

    export async function cloneProject(referenceProjectId: string, projectSettings: ProjectSettings) {
        // todo see ticket #25295 
        projectSettings.defaultEnvironment = projectSettings.environment;
        const response = await axios.post<Project>(`/api/project/${referenceProjectId}/clone`, projectSettings);
        return response.data;
    }

    export function updateProject(projectId: string, projectSettings: ProjectSettings) {
        return axios.put(`/api/project/${projectId}`, projectSettings);
    }

    export async function uploadFile(projectId: string,
                                     env: string,
                                     parentPath: string,
                                     file: File,
                                     onUploadProgress?: (event: ProgressEvent) => void,
                                     cancelToken?: CancelToken) {
        const data = new FormData();
        data.append("parentPath", parentPath ?? '');
        data.append('file', file);

        const config = {
            onUploadProgress,
            cancelToken
        };

        const response = await axios.post<ProjectNode>(`/api/project/${projectId}/env/${env}/file/upload`, data, config);

        return response.data;
    }

    export async function downloadFile(projectId: string, env: string, path: string) {
        const response = await axios.get<string>(`/api/project/${projectId}/env/${env}/file/download?path=${path}`);
        return response.data;
    }

    export const cancelTokenSource = () => axios.CancelToken.source();
}

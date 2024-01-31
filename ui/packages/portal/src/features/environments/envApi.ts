import axios from "axios";

export namespace EnvApi {
    export async function validateId(projectId: string, environmentId: string) {
        const response = await axios.post<string[]>(`/api/project/env/validate/`, null, {
            params: {
                projectId,
                environment: environmentId
            }
        });
        return response.data;
    }

    export async function createEnvironment(projectId: string, environmentId: string) {
        const response = await axios.post<string>(`/api/project/${projectId}/env`, {id: environmentId});
        return response.data;
    }

    export async function deleteEnvironment(projectId: string, environmentId: string) {
        const response = await axios.delete<string>(`/api/project/${projectId}/env/${environmentId}`);
        return response.data;
    }

    export async function duplicateEnvironment(projectId: string, id: string, newEnvironmentId: string) {
        const response = await axios.post<string>(`/api/project/${projectId}/env/${id}/clone`, {newEnvironment: newEnvironmentId});
        return response.data;
    }

    export async function suggestId(projectId: string, environment: string) {
        const response = await axios.post<string>(`/api/project/env/suggest`, null, {params: {environment, projectId}});
        return response.data + '';
    }
}
import axios from "axios";
import { SessionSettingsDto } from "./projectSessionApi";

export namespace EnvSessionApi {
    export async function getSessionSettings(projectId: string, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await axios.get<SessionSettingsDto>(`/api/project/${projectId}/env/${envId}/session/settings`, {params});
        return response.data;
    }
}
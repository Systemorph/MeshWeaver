import axios from "axios";

export namespace ProjectSessionApi {
    export async function getSessionSettings(projectId: string) {
        const response = await axios.get<SessionSettingsDto>(`/api/project/${projectId}/session/settings`, {});
        return response.data;
    }

    export async function getTiers() {
        const response = await axios.get<SessionTierSpecificationDto[]>(`/api/session/tiers`, {});
        return response.data;
    }

    export async function getImages() {
        const response = await axios.get<ImageSettingsDto[]>(`/api/session/images`, {});
        return response.data;
    }
}

export interface SessionSettingsDto {
    readonly image: string;
    readonly imageTag: string;
    readonly tier: SessionTierSpecificationDto;
    readonly sessionIdleTimeout: number;
    readonly applicationIdleTimeout: number;
    readonly inherited: boolean;
}

export interface SessionTierSpecificationDto {
    readonly systemName: string;
    readonly displayName: string;
    readonly minCpu: number;
    readonly cpu: number;
    readonly maxCpu: number;
    readonly minMemory: number;
    readonly memory: number;
    readonly maxMemory: number;
    readonly creditsPerMinute: number;
}

export interface ImageSettingsDto {
    readonly image: string;
    readonly displayName: string;
    readonly imageTags: ImageTagDto[];
}

export interface ImageTagDto {
    readonly imageTag: string;
    readonly isPreRelease: boolean;
}
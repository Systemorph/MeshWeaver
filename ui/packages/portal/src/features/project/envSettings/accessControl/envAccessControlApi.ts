import { get } from "../../../../shared/utils/requestWrapper";
import {
    AccessGroup,
    AccessObject,
    AccessRestriction, AccessUser, GroupMember,
    Permission, UserMembership,
} from "../../../accessControl/accessControl.contract";

export namespace EnvAccessControlApi {
    export async function getRestrictions(projectId: string, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<AccessRestriction[]>(`/api/project/${projectId}/env/${envId}/access/restrictions`, {params});
        return response.data;
    }

    export async function getGroups(projectId: string, envId: string, objectId = envId) {
        const params = {objectId};
        const response = await get<AccessGroup[]>(`/api/project/${projectId}/env/${envId}/access/groups`, {params});
        return response.data;
    }

    export async function getUsers(projectId: string, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<AccessObject[]>(`/api/project/${projectId}/env/${envId}/access/users`, {params});
        return response.data;
    }

    export async function getMembers(projectId: string, accessObject: string, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<GroupMember[]>(`/api/project/${projectId}/env/${envId}/access/members/${accessObject}`, {params});
        return response.data;
    }

    export async function getMemberships(projectId: string, accessObject: string, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<UserMembership[]>(`/api/project/${projectId}/env/${envId}/access/memberships/${accessObject}`, {params});
        return response.data;
    }

    export async function getPermission(projectId: string, permission: Permission, envId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<boolean>(`/api/project/${projectId}/env/${envId}/access/permission/${permission}`, {params});
        return response.data;
    }

    export async function getGroup(projectId: string, envId: string, groupId: string) {
        const response = await get<AccessGroup>(`/api/project/${projectId}/env/${envId}/access/groups/${groupId}`);
        return response.data;
    }

    export async function getUser(projectId: string, envId: string, userId: string, objectId?: string) {
        const params = {objectId};
        const response = await get<AccessUser>(`/api/project/${projectId}/env/${envId}/access/users/${userId}`, {params});
        return response.data;
    }
}
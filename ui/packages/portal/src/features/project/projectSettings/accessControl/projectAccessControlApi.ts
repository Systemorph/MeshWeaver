import { get } from "../../../../shared/utils/requestWrapper";

import {
    AccessGroup,
    AccessObject,
    AccessRestriction,
    AccessUser,
    GroupMember, Permission,
    UserMembership
} from "../../../accessControl/accessControl.contract";

export namespace ProjectAccessControlApi {
    export async function getRestrictions(projectId: string) {
        const response = await get<AccessRestriction[]>(`/api/project/${projectId}/access/restrictions`);
        return response.data;
    }

    export async function getGroups(projectId: string) {
        const response = await get<AccessGroup[]>(`/api/project/${projectId}/access/groups`);
        return response.data;
    }

    export async function getUsers(projectId: string) {
        const response = await get<AccessObject[]>(`/api/project/${projectId}/access/users`);
        return response.data;
    }

    export async function getMembers(projectId: string, accessObject: string) {
        const response = await get<GroupMember[]>(`/api/project/${projectId}/access/members/${accessObject}`);
        return response.data;
    }

    export async function getMemberships(projectId: string, accessObject: string) {
        const response = await get<UserMembership[]>(`/api/project/${projectId}/access/memberships/${accessObject}`);
        return response.data;
    }

    export async function getPermission(projectId: string, permission: Permission) {
        const response = await get<boolean>(`/api/project/${projectId}/access/permission/${permission}`);
        return response.data; 
    }

    export async function getGroup(projectId: string, groupId: string) {
        const response = await get<AccessGroup>(`/api/project/${projectId}/access/groups/${groupId}`);
        return response.data;
    }

    export async function getUser(projectId: string, userId: string) {
        const response = await get<AccessUser>(`/api/project/${projectId}/access/users/${userId}`);
        return response.data;
    }
}
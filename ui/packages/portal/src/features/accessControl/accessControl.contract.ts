import { contractMessage } from "@open-smc/application/src/contractMessage";
import { BaseEvent } from "@open-smc/application/src/application.contract";

export class AccessChangedEvent extends BaseEvent {
    constructor(public objectId: string) {
        super();
    }
}

export type AccessChangeToggle = 'Allow' | 'Deny' | 'Inherit';
export type GroupChangeToggle = 'Remove' | 'Add' | 'Inherit';

@contractMessage("OpenSmc.Notebook.AccessControl.AccessMembershipChangedEvent")
export class AccessMembershipChangedEvent extends AccessChangedEvent {
    constructor(public memberOf: string,
                public accessObject: string,
                public toggle: GroupChangeToggle,
                public objectId: string) {
        super(objectId);
    }
}

@contractMessage("OpenSmc.Notebook.AccessControl.AccessRestrictionChangedEvent")
export class AccessRestrictionChangedEvent extends AccessChangedEvent {
    constructor(public objectId: string,
                public permission: Permission,
                public toggle: AccessChangeToggle) {
        super(objectId);
    }
}

export interface AccessRestriction {
    readonly objectId: string;
    readonly permission: Permission;
    readonly displayName: string;
    readonly description: string;
    readonly toggle: AccessToggle;
    readonly inherited: boolean;
}

export interface ProjectGroup {
    readonly id: string,
    readonly displayName: string,
    readonly description: string,
    readonly permissions: Permission[],
    readonly isSystemGroup: boolean
}

export interface AccessGroup extends AccessObject {
    readonly displayName: string;
    readonly description: string;
}

export interface AccessUser extends AccessObject {
    readonly displayName: string;
    readonly avatarUri: string;
}

export type AccessToggle = 'Allow' | 'Deny';
export type GroupToggle = 'Remove' | 'Add';
export type Permission = 'Read' | 'Edit' | 'Owner' | 'Session' | 'Billing' | 'Public';
export type AccessObjectType = 'User';

export interface AccessObject {
    readonly name: string;
    readonly type: AccessObjectType;
    readonly inherited: boolean;
}

export interface AccessMembership {
    readonly memberOf: string;
    readonly accessObject: string;
    readonly toggle: GroupToggle;
    readonly inherited: boolean;
}

export interface UserMembership extends AccessMembership {
    readonly displayName: string;
    readonly description: string;
}

export interface GroupMember extends AccessMembership {
    readonly displayName: string;
    readonly avatarUri: string;
}
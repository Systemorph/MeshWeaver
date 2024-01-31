import {
    AccessChangeToggle,
    AccessMembershipChangedEvent,
    AccessRestrictionChangedEvent,
    GroupChangeToggle, Permission
} from "./accessControl.contract";
// import { ApplicationHub, ErrorEvent } from "@open-smc/application/applicationHub/applicationHub";

export function getAccessControlEditorApi(viewModelId: string) {
    async function changeAccessMembership(memberOf: string, accessObject: string, toggle: GroupChangeToggle, objectId: string) {
        // const event = new AccessMembershipChangedEvent(memberOf, accessObject, toggle, objectId);
        //
        // await ApplicationHub.sendMessage(viewModelId, event);
        //
        // let unsubscribeSuccess: () => void;
        // let unsubscribeError: () => void;
        //
        // return new Promise((resolve, reject) => {
        //     unsubscribeSuccess = ApplicationHub.onMessage(
        //         viewModelId,
        //         AccessMembershipChangedEvent,
        //         ({status}) => resolve(status),
        //         ({eventId}) => eventId === event.eventId
        //     );
        //
        //     unsubscribeError = ApplicationHub.onMessage<ErrorEvent<AccessMembershipChangedEvent>>(
        //         viewModelId,
        //         ErrorEvent,
        //         ({message}) => reject(message),
        //         ({sourceEvent: {eventId}}) => eventId === event.eventId
        //     );
        // }).finally(() => {
        //     unsubscribeSuccess && unsubscribeSuccess();
        //     unsubscribeError && unsubscribeError();
        // });
    }

    async function changeAccessRestriction(objectId: string, permission: Permission, toggle: AccessChangeToggle) {
        // const event = new AccessRestrictionChangedEvent(objectId, permission, toggle);
        //
        // await ApplicationHub.sendMessage(viewModelId, event);
        //
        // let unsubscribeSuccess: () => void;
        // let unsubscribeError: () => void;
        //
        // return new Promise((resolve, reject) => {
        //     unsubscribeSuccess = ApplicationHub.onMessage(
        //         viewModelId,
        //         AccessRestrictionChangedEvent,
        //         ({status}) => resolve(status),
        //         ({eventId}) => eventId === event.eventId
        //     );
        //
        //     unsubscribeError = ApplicationHub.onMessage<ErrorEvent<AccessRestrictionChangedEvent>>(
        //         viewModelId,
        //         ErrorEvent,
        //         reject,
        //         ({sourceEvent: {eventId}}) => eventId === event.eventId
        //     );
        // }).finally(() => {
        //     unsubscribeSuccess && unsubscribeSuccess();
        //     unsubscribeError && unsubscribeError();
        // });

    }

    function subscribeToProjectPermissionChanges(projectId: string, handler: () => void) {
        // const unsubscribeRestrictionChange = ApplicationHub.onMessage(
        //     viewModelId,
        //     AccessRestrictionChangedEvent,
        //     (event) => handler(),
        //     ({status, objectId}) => status === 'Committed' && objectId === projectId
        // );
        //
        // const unsubscribeMembershipChange = ApplicationHub.onMessage(
        //     viewModelId,
        //     AccessMembershipChangedEvent,
        //     (event) => handler(),
        //     ({status, objectId}) => status === 'Committed' && objectId === projectId
        // );
        //
        // return () => {
        //     unsubscribeRestrictionChange();
        //     unsubscribeMembershipChange();
        // }
    }

    return {
        changeAccessMembership,
        changeAccessRestriction,
        subscribeToProjectPermissionChanges
    }
}

import { useParams } from "react-router-dom";
import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { AccessControlParams } from "../../../accessControl/AccessControlPage";
import { User } from "../../../accessControl/User";
import { useAccessControlEditorApi } from "./useAccessControlEditorApi";
import { useIncrement } from "@open-smc/utils/useIncrement";
import { EnvAccessControlApi } from "./envAccessControlApi";
import { AccessGroup, AccessUser, UserMembership } from "../../../accessControl/accessControl.contract";
import { useEnvSettingsState } from "../useEnvSettingsState";
import { useThrowAsync } from "@open-smc/utils/useThrowAsync";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function EnvUserPage() {
    const {project} = useProject();
    const {envId, node, permissions: {isOwner}} = useEnvSettingsState();
    const {userId} = useParams<AccessControlParams>();
    const [user, setUser] = useState<AccessUser>();
    const [memberships, setMemberships] = useState<UserMembership[]>();
    const [groups, setGroups] = useState<AccessGroup[]>();
    const [ready, setReady] = useState(false);
    const [loading, setLoading] = useState(false);
    const [refreshed, refresh] = useIncrement();
    const accessControlEditorApi = useAccessControlEditorApi();
    const throwAsync = useThrowAsync();
    const [error, setError] = useState(null);

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const user = await EnvAccessControlApi.getUser(project.id, envId, userId, node?.id);
                const memberships = await EnvAccessControlApi.getMemberships(project.id, user.name, envId, node?.id);
                const groups = await EnvAccessControlApi.getGroups(project.id, envId, node?.id);
                setUser(user);
                setMemberships(memberships);
                setGroups(groups);
            } catch (error) {
                setError(error);
            }

            setLoading(false);
            setReady(true);
        })();
    }, [project.id, userId, envId, node, refreshed]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (error) {
        switch (error) {
            case 404:
                return <div>User not found</div>
            default:
                throwAsync(error)
        }
    }

    return (
        <User
            user={user}
            memberships={memberships}
            groups={groups}
            editable={isOwner}
            onChangeMembership={
                async (memberOf, toggle) => {
                    try {
                        const status = await accessControlEditorApi.changeAccessMembership(memberOf, user.name, toggle, node ? node.id : envId);
                        refresh();
                    } catch (error) {
                    }
                }
            }
            canOverride={true}
            loading={loading}
        />
    );
}
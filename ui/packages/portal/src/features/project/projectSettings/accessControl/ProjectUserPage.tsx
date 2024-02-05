import { useParams } from "react-router-dom";
import { useEffect, useState } from "react";
import { AccessControlParams } from "../../../accessControl/AccessControlPage";
import { User } from "../../../accessControl/User";
import { useProject } from "../../projectStore/hooks/useProject";
import { useAccessControlEditorApi } from "../../projectStore/hooks/useAccessControlEditorApi";
import { useIncrement } from "@open-smc/utils/src/useIncrement";
import { AccessGroup, AccessUser, UserMembership } from "../../../accessControl/accessControl.contract";
import { ProjectAccessControlApi } from "./projectAccessControlApi";
import { useProjectPermissions } from "../../projectStore/hooks/useProjectPermissions";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function ProjectUserPage() {
    const {project} = useProject();
    const {userId} = useParams<AccessControlParams>();
    const [user, setUser] = useState<AccessUser>();
    const [memberships, setMemberships] = useState<UserMembership[]>();
    const [groups, setGroups] = useState<AccessGroup[]>();
    const {isOwner} = useProjectPermissions();
    const [ready, setReady] = useState(false);
    const [loading, setLoading] = useState(false);
    const [refreshed, refresh] = useIncrement();
    const [error, setError] = useState(null);
    const accessControlEditorApi = useAccessControlEditorApi();

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const user = await ProjectAccessControlApi.getUser(project.id, userId);
                const memberships = await ProjectAccessControlApi.getMemberships(project.id, user.name);
                const groups = await ProjectAccessControlApi.getGroups(project.id);
                setUser(user);
                setMemberships(memberships);
                setGroups(groups);
            } catch (error) {
                setError(error);
            }
            setLoading(false);
            setReady(true);
        })();
    }, [project.id, userId, refreshed]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (error) {
        switch (error) {
            case 404:
                return <div>User not found</div>
            default:
                throw error
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
                        const status = await accessControlEditorApi.changeAccessMembership(memberOf, user.name, toggle, project.id);
                        refresh();
                    } catch (error) {
                    }
                }
            }
            loading={loading}
        />
    );
}
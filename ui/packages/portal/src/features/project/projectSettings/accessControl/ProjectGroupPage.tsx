import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { AccessControlParams } from "../../../accessControl/AccessControlPage";
import { Group } from "../../../accessControl/Group";
import { useProject } from "../../projectStore/hooks/useProject";
import { useAccessControlEditorApi } from "../../projectStore/hooks/useAccessControlEditorApi";
import { useIncrement } from "@open-smc/utils/src/useIncrement";
import { ProjectAccessControlApi } from "./projectAccessControlApi";
import { AccessGroup, GroupMember } from "../../../accessControl/accessControl.contract";
import { useProjectPermissions } from "../../projectStore/hooks/useProjectPermissions";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function ProjectGroupPage() {
    const {project} = useProject();
    const {groupId} = useParams<AccessControlParams>();
    const [group, setGroup] = useState<AccessGroup>();
    const [memberships, setMemberships] = useState<GroupMember[]>();
    const {isOwner} = useProjectPermissions();
    const [ready, setReady] = useState(false);
    const [loading, setLoading] = useState(true);
    const [refreshed, refresh] = useIncrement();
    const accessControlEditorApi = useAccessControlEditorApi();
    const [error, setError] = useState(null);

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const group = await ProjectAccessControlApi.getGroup(project.id, groupId);
                const memberships = await ProjectAccessControlApi.getMembers(project.id, groupId);
                setMemberships(memberships);
                setGroup(group);

            } catch (error) {
                setError(error);
            }
            setLoading(false);
            setReady(true);
        })();
    }, [project.id, groupId, refreshed]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (error) {
        switch (error) {
            case 404:
                return <div>Group not found</div>
            default:
                throw error
        }
    }

    return (
        <Group
            group={group}
            memberships={memberships}
            editable={isOwner}
            onChange={
                async (accessObject, toggle) => {
                    try {
                        const status = await accessControlEditorApi.changeAccessMembership(group.name, accessObject, toggle, project.id)
                        refresh();
                    } catch (error) {

                    }
                }
            }
            loading={loading}
        />
    );
}
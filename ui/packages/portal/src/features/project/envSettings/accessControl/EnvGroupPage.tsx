import { useParams } from "react-router-dom";
import { useEffect, useState } from "react";
import { Group } from "../../../accessControl/Group";
import { AccessControlParams } from "../../../accessControl/AccessControlPage";
import { useProject } from "../../projectStore/hooks/useProject";
import { useAccessControlEditorApi } from "./useAccessControlEditorApi";
import { useIncrement } from "@open-smc/utils/useIncrement";
import { EnvAccessControlApi } from "./envAccessControlApi";
import { AccessGroup, GroupMember } from "../../../accessControl/accessControl.contract";
import { useEnvSettingsState } from "../useEnvSettingsState";
import { useThrowAsync } from "@open-smc/utils/useThrowAsync";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function EnvGroupPage() {
    const {project} = useProject();
    const {groupId} = useParams<AccessControlParams>();
    const {envId, node, permissions: {isOwner}} = useEnvSettingsState();
    const [group, setGroup] = useState<AccessGroup>();
    const [memberships, setMemberships] = useState<GroupMember[]>();
    const [ready, setReady] = useState(false);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);
    const [refreshed, refresh] = useIncrement();
    const accessControlEditorApi = useAccessControlEditorApi();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const group = await EnvAccessControlApi.getGroup(project.id, envId, groupId);
                const memberships = await EnvAccessControlApi.getMembers(project.id, groupId, envId, node?.id);
                setGroup(group);
                setMemberships(memberships);
            } catch (error) {
                setError(error);
            }
            setLoading(false);
            setReady(true);
        })();
    }, [project.id, groupId, envId, node, refreshed]);

    if (!ready) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (error) {
        switch (error) {
            case 404:
                return <div>Group not found</div>
            default:
                throwAsync(error)
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
                        const status = await accessControlEditorApi.changeAccessMembership(group.name, accessObject, toggle, node ? node.id : envId);
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
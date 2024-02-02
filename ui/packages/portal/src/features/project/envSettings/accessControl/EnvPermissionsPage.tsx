import { useEffect, useState } from "react";
import { Permissions } from "../../../accessControl/Permissions";
import { useProject } from "../../projectStore/hooks/useProject";
import { useAccessControlEditorApi } from "./useAccessControlEditorApi";
import { EnvAccessControlApi } from "./envAccessControlApi";
import { useIncrement } from "@open-smc/utils/src/useIncrement";
import { AccessRestriction } from "../../../accessControl/accessControl.contract";
import { useEnvSettingsState } from "../useEnvSettingsState";
import { useThrowAsync } from "@open-smc/utils/src/useThrowAsync";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function EnvPermissionsPage() {
    const {project} = useProject();
    const {envId, node, permissions: {isOwner}} = useEnvSettingsState();
    const accessControlEditorApi = useAccessControlEditorApi();
    const [restrictions, setRestrictions] = useState<AccessRestriction[]>();
    const [refreshed, refresh] = useIncrement();
    const [loading, setLoading] = useState(false);
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const restrictions = await EnvAccessControlApi.getRestrictions(project.id, envId, node?.id);
                setRestrictions(restrictions);
            } catch (error) {
                throwAsync(error)
            }
            setLoading(false);
        })();
    }, [project.id, envId, node, refreshed]);

    if (!restrictions) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <Permissions
            restrictions={restrictions}
            editable={isOwner}
            onChange={
                async (permission, toggle) => {
                    try {
                        const status = await accessControlEditorApi.changeAccessRestriction(node ? node.id : envId, permission, toggle);
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

import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { Permissions } from "../../../accessControl/Permissions";
import { useAccessControlEditorApi } from "../../projectStore/hooks/useAccessControlEditorApi";
import { useIncrement } from "@open-smc/utils/src/useIncrement";
import { ProjectAccessControlApi } from "./projectAccessControlApi";
import { AccessRestriction } from "../../../accessControl/accessControl.contract";
import { useProjectPermissions } from "../../projectStore/hooks/useProjectPermissions";
import { useThrowAsync } from "@open-smc/utils/src/useThrowAsync";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function ProjectPermissionsPage() {
    const {project} = useProject();
    const accessControlEditorApi = useAccessControlEditorApi();
    const [restrictions, setRestrictions] = useState<AccessRestriction[]>();
    const [refreshed, refresh] = useIncrement();
    const [loading, setLoading] = useState(false);
    const {isOwner} = useProjectPermissions();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            setLoading(true);
            try {
                const restrictions = await ProjectAccessControlApi.getRestrictions(project.id)
                setRestrictions(restrictions);
            } catch (error) {
                throwAsync(error);
            }

            setLoading(false);
        })();
    }, [project.id, refreshed]);

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
                        const status = await accessControlEditorApi.changeAccessRestriction(project.id, permission, toggle)
                        refresh();
                    } catch (error) {
                    }
                }
            }
            loading={loading}
        />
    );
}

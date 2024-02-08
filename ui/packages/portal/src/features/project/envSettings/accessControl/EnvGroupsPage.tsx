import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { Groups } from "../../../accessControl/Groups";
import { EnvAccessControlApi } from "./envAccessControlApi";
import { AccessGroup } from "../../../accessControl/accessControl.contract";
import { useEnvSettingsState } from "../useEnvSettingsState";
import { useThrowAsync } from "@open-smc/utils/src/useThrowAsync";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function EnvGroupsPage() {
    const {project} = useProject();
    const {envId, node} = useEnvSettingsState();
    const [groups, setGroups] = useState<AccessGroup[]>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const groups = await EnvAccessControlApi.getGroups(project.id, envId, node?.id);
                setGroups(groups);
            } catch (error) {
                throwAsync(error);
            }
        })();
    }, [project.id, envId, node]);

    if (!groups) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <Groups groups={groups}/>
    );
}
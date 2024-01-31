import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { Groups } from "../../../accessControl/Groups";
import { ProjectAccessControlApi } from "./projectAccessControlApi";
import { AccessGroup } from "../../../accessControl/accessControl.contract";
import { useThrowAsync } from "@open-smc/utils/useThrowAsync";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function ProjectGroupsPage() {
    const {project} = useProject();
    const [groups, setGroups] = useState<AccessGroup[]>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const groups = await ProjectAccessControlApi.getGroups(project.id)
                setGroups(groups);
            } catch (error) {
                throwAsync(error);
            }

        })();
    }, [project.id]);

    if (!groups) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <Groups groups={groups}/>
    );
}
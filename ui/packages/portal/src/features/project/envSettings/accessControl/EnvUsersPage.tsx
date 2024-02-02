import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { Users } from "../../../accessControl/Users";
import { EnvAccessControlApi } from "./envAccessControlApi";
import { AccessObject } from "../../../accessControl/accessControl.contract";
import { useEnvSettingsState } from "../useEnvSettingsState";
import { useThrowAsync } from "@open-smc/utils/src/useThrowAsync";

export function EnvUsersPage() {
    const {project} = useProject();
    const {envId, node} = useEnvSettingsState();
    const [users, setUsers] = useState<AccessObject[]>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const users = await EnvAccessControlApi.getUsers(project.id, envId, node?.id);
                setUsers(users);
            } catch (error) {
                throwAsync(error);
            }
        })();
    }, [project.id, envId, node]);

    return (
        <Users users={users}/>
    );
}
import { useEffect, useState } from "react";
import { useProject } from "../../projectStore/hooks/useProject";
import { Users } from "../../../accessControl/Users";
import { AccessObject } from "../../../accessControl/accessControl.contract";
import { ProjectAccessControlApi } from "./projectAccessControlApi";
import { useThrowAsync } from "@open-smc/utils/src/";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

export function ProjectUsersPage() {
    const {project} = useProject();
    const [users, setUsers] = useState<AccessObject[]>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const users = await ProjectAccessControlApi.getUsers(project.id)
                setUsers(users);
            } catch (error) {
                throwAsync(error);
            }

        })();
    }, [project.id]);

    if (!users) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <Users users={users}/>
    );
}
import { Outlet, useParams } from "react-router-dom";
import { EnvContent } from "./EnvContent";
import { useEffect } from "react";
import { useEnv } from "../projectStore/hooks/useEnv";

type EnvContentPageParams = {
    envId: string;
}

export function EnvContentPage() {
    const {envId: envIdParam} = useParams<EnvContentPageParams>();
    const {envId, setEnvId} = useEnv();

    useEffect(() => {
        if (envIdParam !== envId) {
            setEnvId(envIdParam);
        }
    }, [envId, setEnvId, envIdParam]);

    return (
        <EnvContent>
            <Outlet/>
        </EnvContent>
    );
}
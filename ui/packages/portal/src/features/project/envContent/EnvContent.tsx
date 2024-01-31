import { useEnv } from "../projectStore/hooks/useEnv";
import { PropsWithChildren } from "react";
import page from "../pages/page.module.scss";
import errorpage from "../../errorpage.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export function EnvContent({children}: PropsWithChildren) {
    const {isLoading, env, error} = useEnv();

    if (isLoading) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (error) {
        return <EnvError/>
    }

    if (env.isCloning) {
        return (
            <div className={page.cloning}>
                <div className={page.content}>
                    <h1 className={page.title}>Cloning...</h1>
                    <p className={page.description}>We are hard at work cloning your environment, please come back in a
                        bit.</p>
                </div>
            </div>
        );
    }

    return <>{children}</>;
}

function EnvError() {
    const {envId, error} = useEnv();

    switch (error) {
        case 404:
            return (
                <div className={errorpage.envNotFound}>
                    <p title={`${error}: Environment not found`} className={errorpage.description}>Yikes, it seems we
                        cannot locate the environment <strong>{envId}</strong>.</p>
                </div>
            );
        case 403:
            return (
                <div className={errorpage.envAccessDenied}>
                    <p title={`${error}: Environment access denied`} className={errorpage.description}>Oh no, it looks
                        like you do not have access to the environment <strong>{envId}</strong> yet.</p>
                </div>
            );
        default:
            return (
                <div className={errorpage.envDefaultError}>
                    <p title={`${error}: Failed to load the environment`} className={errorpage.description}>Mercury must
                        be in Retrograde, because we failed to load the environment.</p>
                </div>
            );
    }
}
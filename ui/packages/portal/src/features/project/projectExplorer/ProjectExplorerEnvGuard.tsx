import { ReactNode } from "react";
import { useEnv } from "../projectStore/hooks/useEnv";
import errorpage from "../../errorpage.module.scss";
import styles from "./project-explorer.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import { FormHeader } from "../../../shared/components/sideMenuComponents/FormHeader";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";

interface ProjectExplorerEnvGuardProps {
    render: () => JSX.Element;
}

export function ProjectExplorerEnvGuard({render}: ProjectExplorerEnvGuardProps) {
    const {isLoading, env} = useEnv();
    const {closeMenu} = useSideMenu();

    function renderNoEnv(content: ReactNode) {
        return (
            <>
                <FormHeader text={'Explorer'} onClose={closeMenu}/>
                {content}
            </>
        );
    }

    if (isLoading) {
        return renderNoEnv(
            <div className={styles.sideContainer}>
                <div className={loader.sideLoading}>Loading...</div>
            </div>
        );
    }

    if (!env) {
        return renderNoEnv(
            <div className={errorpage.sideContainer}>
                <p className={errorpage.description}>Please select an existing environment to see the files</p>
            </div>
        );
    }

    if (env.isCloning) {
        return renderNoEnv(
            <div className={errorpage.sideContainer}>
                <div className={errorpage.description}>Files will be available after the cloning is complete.</div>
            </div>
        );
    }

    return render();
}


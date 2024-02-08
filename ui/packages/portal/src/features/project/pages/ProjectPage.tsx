import styles from '../layout.module.scss';
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";
import { Outlet, useParams } from "react-router-dom";
import { useEffect, useState } from "react";
import { ProjectContextProvider } from "../projectStore/projectStore";
import { getInitialState, loadEnv, ProjectState } from "../projectStore/projectState";
import { SideMenu } from "../../components/sideMenu/SideMenu";
import { ProjectFooter } from "../components/footer/ProjectFooter";
import { EnvironmentsExplorer } from "../../environments/EnvironmentsExplorer";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";
import { projectMenuNames, ProjectSideBar } from "../components/sideBar/ProjectSideBar";
import { ProjectError } from "./ProjectError";
import classNames from "classnames";
import { useSubscribeToProjectPermissionsChange } from "../projectStore/hooks/useSubscribeToProjectPermissionsChange";
import { SideMenuStoreProvider } from "../../components/sideMenu/SideMenuStore";
import { useUserState } from "../projectStore/hooks/useUserState";
import { useEnv } from "../projectStore/hooks/useEnv";
import { useProject } from "../projectStore/hooks/useProject";
import { Footer } from "../../components/footer/Footer";
import { ProjectExplorerStarter } from "../projectExplorer/ProjectExplorerStarter";
import { ProjectExplorerEnvGuard } from "../projectExplorer/ProjectExplorerEnvGuard";

function Project() {
    const {project} = useProject();
    const {env, setEnv} = useEnv();

    useEffect(() => {
        if (env?.isCloning) {
            const timer = setInterval(async () => {
                const newEnv = await loadEnv(project.id, env.id);

                if (newEnv.error || !newEnv.env.isCloning) {
                    setEnv({envId: env.id, ...newEnv});
                }
            }, 1000);

            return () => clearInterval(timer);
        }
    }, [env]);

    return (
        <SideMenuStoreProvider>
            <ProjectInner/>
        </SideMenuStoreProvider>
    );
}

function ProjectInner() {
    const {currentMenu} = useSideMenu();
    const subscribeToPermissionsChange = useSubscribeToProjectPermissionsChange();

    useEffect(subscribeToPermissionsChange, []);

    useUserState();

    const menus = {
        [projectMenuNames.explorer]:
            (
                <ProjectExplorerEnvGuard render={() => <ProjectExplorerStarter/>}/>
            ),
        [projectMenuNames.environments]:
            (
                <EnvironmentsExplorer/>
            ),
    }

    const className = classNames(styles.main, {sideMenuOpen: !!currentMenu});

    return (
        <div className={className}>
            <SideMenu menus={menus}>
                <ProjectSideBar/>
            </SideMenu>
            <div id='scrollable' className={styles.content}>
                <Outlet/>
            </div>
        </div>
    );
}

type ProjectPageParams = {
    projectId: string;
    envId?: string;
}

export function ProjectPage() {
    const {projectId, envId} = useParams<ProjectPageParams>();
    const [error, setError] = useState<unknown>();
    const [initialState, setInitialState] = useState<ProjectState>();

    useEffect(() => {
        (async () => {
            try {
                const initialState = await getInitialState(projectId, envId);
                setInitialState(initialState);
            } catch (error) {
                setError(error);
            }
        })();
    }, [projectId]);

    if (!initialState && !error) {
        return <div className={loader.loading}>Loading...</div>
    }

    if (error) {
        return (
            <>
                <ProjectError error={error}/>
                <Footer/>
            </>
        );
    }

    return (
        <ProjectContextProvider state={initialState}>
            <Project/>
            <ProjectFooter/>
        </ProjectContextProvider>
    );
}
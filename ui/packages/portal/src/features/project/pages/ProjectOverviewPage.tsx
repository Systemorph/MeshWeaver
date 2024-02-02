import { useEffect, useState } from "react";
import { ProjectApi } from "../../../app/projectApi";
import { useProject } from "../projectStore/hooks/useProject";
import { getCompiler, getParser } from "../../notebook/markdownParser";
import { Html } from "@open-smc/ui-kit/src/components/Html";
import { Helmet } from "react-helmet";
import { useSideMenu } from "../../components/sideMenu/hooks/useSideMenu";
import { CloneProjectForm } from "../editProject/CloneProjectForm";
import { isEmpty } from "lodash";
import { useNavigate } from "react-router-dom";
import { AxiosError } from "axios";
import { useEnv } from "../projectStore/hooks/useEnv";
import styles from "./project-overview.module.scss";
import page from "../../errorpage.module.scss";
import mdStyles from "../../../shared/components/markdown.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss"
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import classNames from "classnames";

export function ProjectOverviewPage() {
    const [overview, setOverview] = useState<string>();
    const [overviewError, setOverviewError] = useState<JSX.Element>();
    const {project} = useProject();
    const {env} = useEnv();
    const {showMenu, closeMenu, hideMenu} = useSideMenu();
    const navigate = useNavigate();

    useEffect(() => {
        document.title = `${project.name} - Systemorph Cloud`;
    }, []);

    useEffect(() => {
        (async () => {
            try {
                const overview = await ProjectApi.getOverview(project.id, env.id);
                const parser = getParser(false, project.id, env.id, '/');
                const compiler = getCompiler(false);
                setOverview(compiler(parser(overview)));
            } catch (error) {
                setOverviewError(getOverviewError(error));
            }
        })();
    }, [project.id, env.id]);

    if (!overview && !overviewError) {
        return <div className={loader.loading}>Loading...</div>;
    }

    if (overviewError) {
        return <div className={page.errorContainer}>{overviewError}</div>;
    }

    return (
        <>
            <Helmet>
                <base href={`${document.location.origin}/project/${project.id}/env/${env.id}/`}/>
            </Helmet>
            <div className={styles.wrapper}>
                <div className={styles.overview}>
                    <div className={styles.column}>
                        <h1 className={styles.name}>{project.name}</h1>
                        <h2 className={styles.abstract}>{project.abstract}</h2>
                        <Button className={classNames(styles.cloneButton, button.button)} data-qa-btn-clone
                                label="clone project"
                                onClick={() => {
                                    showMenu(<CloneProjectForm
                                        projectId={project.id}
                                        onClose={closeMenu}
                                        onCancel={closeMenu}
                                        onFinish={
                                            (id) => {
                                                navigate(`/project/${id}`);
                                                closeMenu();
                                            }
                                        }
                                    />)
                                }}>
                        </Button>
                    </div>
                    <div className={styles.column}>
                        {!isEmpty(project.thumbnail?.trim()) &&
                            <img src={project.thumbnail} className={styles.image} alt={project.name}/>}
                    </div>
                </div>
            </div>

            <div className={styles.content}>
                <Html html={overview} className={mdStyles.markdownContent}/>
            </div>
        </>
    );
}

function getOverviewError(error: unknown) {
    if ((error as AxiosError).response) {
        const status = (error as AxiosError)?.response?.status;

        switch (status) {
            case 404:
                return (
                    <div className={page.projectOverviewAccessDenied}>
                        <p title={`${status}: Access denied`} className={page.description}>Bummer, it looks like you do
                            not have access yet.</p>
                    </div>
                );
            default:
                break;
        }
    }

    return (
        <div className={page.projectOverviewDefaultError}>
            <p className={page.description}>Failed to load project overview</p>
        </div>
    );
}
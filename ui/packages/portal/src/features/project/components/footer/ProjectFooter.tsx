import { Footer } from "../../../components/footer/Footer";
import styles from "../../../components/footer/footer.module.scss";
import { useProject } from "../../projectStore/hooks/useProject";
import { useEnv } from "../../projectStore/hooks/useEnv";

// TODO uncomment it when functionality will be available. Requested by Ksenia R. (30.08.2022, aberezutsky)
export function ProjectFooter() {
    const {project} = useProject();
    const {env} = useEnv();

    return (
        <Footer>
            {project &&
              <li className={styles.item}>
                <i className="sm sm-overview"/>
                <span className={styles.name}>{project.name}</span>
              </li>
            }
            {env &&
              <li className={styles.item}>
                <i className="sm sm-enviroment"/>
                <span className={styles.name}>{env.id}</span>
              </li>
            }
            {/*<ul className={styles.breadcrumbs}>*/}
            {/*    <li className={styles.breadcrumb}>*/}
            {/*        <i className="sm sm-folder-open"/>*/}
            {/*        <Breadcrumbs/>*/}
            {/*    </li>*/}
            {/*</ul>*/}
        </Footer>
    );
}


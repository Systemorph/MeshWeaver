import { NavLink, Outlet } from "react-router-dom";
import tabs from '../../../shared/components/tabs.module.scss';
import styles from "../editProject/setupProject.module.scss";
import classNames from "classnames";
import {ErrorBoundary} from "@open-smc/ui-kit/src/components/ErrorBoundary";
import { useProjectPermissions } from "../projectStore/hooks/useProjectPermissions";

export function ProjectSettingsPage() {
    const {isOwner} = useProjectPermissions();
    const className: any = ({isActive}: any) => classNames(tabs.link, {'active': isActive});

    return (
        <ErrorBoundary fallback={getFallback}>
            <div className={classNames(styles.settings, 'settings')}>
                <div>
                    <h1 className={styles.title}>Project settings</h1>
                </div>

                <ul className={tabs.list}>
                    <li className={tabs.item}>
                        <NavLink data-qa-tab-general to="general" className={className}>General</NavLink>
                    </li>
                    <li className={tabs.item}>
                        <NavLink data-qa-tab-general to="session" className={className}>Session</NavLink>
                    </li>
                    <li className={tabs.item}>
                        <NavLink data-qa-tab-ac to="access" className={className}>Access Control</NavLink>
                    </li>
                    {isOwner &&
                        <li><NavLink data-qa-tab-billing to="billing" className={className}>Billing</NavLink></li>
                    }
                </ul>
                <Outlet/>
            </div>
        </ErrorBoundary>
    );
}

function getFallback(error: unknown) {
    switch (error) {
        case 403:
            return <div className={styles.error}>Access denied</div>
        default:
            return <div className={styles.error}>Failed to load content</div>
    }
}

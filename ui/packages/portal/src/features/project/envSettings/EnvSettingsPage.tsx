import { NavLink, Outlet, useParams } from "react-router-dom";
import styles from "../editProject/setupProject.module.scss";
import tabs from '../../../shared/components/tabs.module.scss';
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import classNames from "classnames";
import { useEffect, useState } from "react";
import { EnvSettingsState, EnvSettingsStore, getInitialState } from "./envSettingsStore";
import { useProject } from "../projectStore/hooks/useProject";
import { useEnvSettingsState } from "./useEnvSettingsState";
import { ErrorBoundary } from "@open-smc/ui-kit/components/ErrorBoundary";
import { useThrowAsync } from "@open-smc/utils/useThrowAsync";

export type EnvSettingsPageParams = {
    settingsEnvId: string;
    nodeId?: string;
}

export function EnvSettingsPage() {
    const {nodeId} = useParams<EnvSettingsPageParams>();

    return (
        <ErrorBoundary fallback={e => nodeId ? getNodeError(e) : getEnvError(e)}>
            <EnvSettings/>
        </ErrorBoundary>
    );
}

export function EnvSettings() {
    const {project} = useProject();
    const {settingsEnvId, nodeId} = useParams<EnvSettingsPageParams>();
    const [initialState, setInitialState] = useState<EnvSettingsState>();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const initialState = await getInitialState(project.id, settingsEnvId, nodeId);
                setInitialState(initialState);
            } catch (error) {
                throwAsync(error);
            }
        })();
    }, [project.id, settingsEnvId, nodeId]);

    if (!initialState) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <EnvSettingsStore initialState={initialState}>
            <div className={classNames(styles.settings, 'settings')}>
                <EnvSettingsHeader/>
                <Outlet/>
            </div>
        </EnvSettingsStore>
    );
}

export function EnvSettingsHeader() {
    const {envId, node} = useEnvSettingsState();
    const className: any = ({isActive}: any) => classNames(tabs.link, {'active': isActive});

    return (
        <>
            <div>
                <h1 className={styles.title}>Environment settings: <span
                    data-qa-ac={node ? 'Node' : 'Env'}>{node ? node.name : envId}</span></h1>
            </div>

            <ul className={tabs.list}>
                {(!node || node.kind === 'Folder' || node.kind === 'Notebook') &&
                    <li><NavLink to="session" className={className}>Session</NavLink></li>
                }
                <li className={tabs.item}>
                    <NavLink to="access" className={className}>Access Control</NavLink>
                </li>
            </ul>
        </>
    );
}

function getNodeError(error: unknown) {
    switch (error) {
        case 404:
            return (
                <div>Node not found</div>
            );
        case 403:
            return (
                <div>Forbidden</div>
            );
        default:
            return (
                <div>Failed to load node</div>
            );
    }
}

function getEnvError(error: unknown) {
    switch (error) {
        case 404:
            return (
                <div>Env not found</div>
            );
        case 403:
            return (
                <div>Forbidden</div>
            );
        default:
            return (
                <div>Failed to load env</div>
            );
    }
}
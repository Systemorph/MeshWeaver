import { useParams, useSearchParams } from "react-router-dom";
import { ErrorBoundary } from "@open-smc/ui-kit/components/ErrorBoundary";
import { SmappContext } from "./SmappContext";
import { Smapp } from "./Smapp";
import page from "../errorpage.module.scss";

type SmappPageParams = {
    projectId: string;
    envId: string;
    '*': string; // notebook path
}

export function SmappPage() {
    const {projectId, envId, '*': notebookPath} = useParams<SmappPageParams>();
    const [searchParams] = useSearchParams();
    const sessionId = searchParams.get('sessionId');

    return (
        <ErrorBoundary fallback={error => getError(error as number)}>
            <SmappContext
                projectId={projectId}
                envId={envId}
                notebookPath={notebookPath}
                sessionId={sessionId}>
                <Smapp/>
            </SmappContext>
        </ErrorBoundary>
    );
}

function getError(status: number) {
    switch (status) {
        case 404:
            return (
                <div className={page.projectNotFound}>
                    <p title={`${status}: Not found`} className={page.description}>Sorry, we tried but could not
                        locate the smapp you requested.</p>
                </div>
            );
        case 403:
            return (
                <div className={page.projectAccessDenied}>
                    <p title={`${status}: Access denied`} className={page.description}>Bummer, it looks like you do not
                        have access to this awesome smapp yet.</p>
                </div>
            );
        default:
            return (
                <div className={page.projectDefaultError}>
                    <p className={page.description}>Mercury must be in
                        Retrograde, because we failed to load the smapp.</p>
                </div>
            );
    }
}
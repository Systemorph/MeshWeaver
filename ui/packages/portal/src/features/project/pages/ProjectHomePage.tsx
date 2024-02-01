import { ProjectOverviewPage } from "./ProjectOverviewPage";
import { EnvContent } from "../envContent/EnvContent";

export function ProjectHomePage() {
    return (
        <EnvContent>
            <ProjectOverviewPage/>
        </EnvContent>
    );
}
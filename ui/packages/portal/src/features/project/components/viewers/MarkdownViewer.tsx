import { useState, useEffect } from "react";
import { ProjectApi } from "../../../../app/projectApi";
import { Html } from "@open-smc/ui-kit/src/components/Html";
import { getParser, getCompiler } from "../../../notebook/markdownParser";
import { FileModel } from "../../projectExplorer/projectExplorerStore/fileExplorerState";
import { useEnvironmentId } from "../../projectStore/hooks/useEnvironmentId";
import { useProject } from "../../projectStore/hooks/useProject";
import styles from "./markdownViewer.module.scss";
import mdStyles from "../../../../shared/components/markdown.module.scss";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";

interface FileProps {
    file: FileModel;
}

export default function MarkdownViewer({file}: FileProps) {
    const [content, setContent] = useState<string>();
    const [loading, setLoading] = useState(true);
    const {project} = useProject();
    const environmentId = useEnvironmentId();

    useEffect(() => {
        (async () => {
            setLoading(true);
            const parser = getParser(false, project.id, environmentId, file.path);
            const compiler = getCompiler(false);
            const content = await ProjectApi.downloadFile(
                project.id,
                environmentId,
                file.path
            );
            setContent(compiler(parser(content)));
            setLoading(false);
        })();
    }, [project.id, environmentId]);

    if (loading) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <div className={styles.content}>
            <Html html={content} className={mdStyles.markdownContent}/>
        </div>
    );
}

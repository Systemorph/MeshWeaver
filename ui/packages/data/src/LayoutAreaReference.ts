import { useAppSelector } from "packages/application/src/app/hooks";
import { JSONPath } from "jsonpath-plus";
import { LayoutArea, WorkspaceReference } from "src/contract/application.contract";
import { renderControl } from "src/renderControl";

interface LayoutAreaReferenceProps {
    workspaceReference: WorkspaceReference;
}

export function LayoutAreaReference({workspaceReference}: LayoutAreaReferenceProps) {
    const {address, path} = workspaceReference;
    const area = useAppSelector(
        ({data: {workspace}}) => JSONPath<LayoutArea[]>({path, json: workspace})[0]
    );

    if (!area?.control) {
        return null;
    }

    return renderControl(area.control);
}
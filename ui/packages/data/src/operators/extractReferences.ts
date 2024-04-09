import { walk, WalkNode } from 'walkjs';
import { WorkspaceReference } from "../contract/WorkspaceReference";

export const extractReferences = (data: unknown) => {
    const references: Array<[path: string, reference: WorkspaceReference]> = [];

    walk(
        data,
        {
            onVisit: {
                filters: node => node.val instanceof WorkspaceReference,
                callback:
                    node =>
                        references.push([
                                node.getPath(toJsonPointer),
                                node.val
                            ])
            }
        }
    );

    return references;
}

const toJsonPointer = (node: WalkNode) => `/${node.key}`;
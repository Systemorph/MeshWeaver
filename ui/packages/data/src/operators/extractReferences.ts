import { walk, WalkNode } from 'walkjs';
import { WorkspaceReferenceBase } from "../contract/WorkspaceReferenceBase";

export const extractReferences = (data: unknown) => {
    const references: Array<[path: string, reference: WorkspaceReferenceBase]> = [];

    walk(
        data,
        {
            onVisit: {
                filters: node => node.val instanceof WorkspaceReferenceBase,
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
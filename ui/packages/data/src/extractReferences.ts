import { WorkspaceReference } from "./data.contract";
import { walk } from 'walkjs';

export type ExtractedReference = {
    path: string;
    reference: WorkspaceReference;
}

export const extractReferences = (data: unknown) => {
    const references: ExtractedReference[] = [];

    walk(
        data,
        {
            onVisit: {
                filters: node => node.val instanceof WorkspaceReference,
                callback:
                    node =>
                        references.push(
                            {
                                path: node.getPath(),
                                reference: node.val
                            }
                        )
            }
        }
    );

    return references;
}
import { SessionStatus } from "../features/notebook/notebookEditor/notebookEditor.contract";

export type ElementKind = 'code' | 'markdown';
export type EvaluationStatus = 'Idle' | 'Pending' | 'Evaluating';

export interface SessionDescriptor {
    readonly id?: string;
    readonly started?: string;
    readonly startedBy?: string;
    readonly stopped?: string;
    readonly stoppedBy?: string;
    readonly specification?: SessionSpecification;
    readonly status: SessionStatus;
    readonly statusMessage?: string;
    readonly moduleReferences?: Record<string, string>;
    readonly duration?: number;
    readonly creditsUsed?: number;
}

export interface SessionSpecification {
    readonly image: string;
    readonly imageTag: string;
    readonly tier: string;
    readonly cpu: number;
    readonly memory: number;
    readonly creditsPerMinute?: number;
}
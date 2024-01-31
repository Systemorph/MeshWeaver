import { EventStatus } from "../application.contract";

// TODO: legacy (8/10/2023, akravets)
export function validateStatus(status: EventStatus) {
    if (status !== 'Committed') {
        switch (status) {
            case 'AccessDenied':
                throw 'Access Denied';
            case 'InvalidSubscription':
                throw 'Invalid subscription';
            default:
                throw 'Unexpected error occurred';
        }
    }
}
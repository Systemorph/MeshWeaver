import { SessionDescriptor } from "../../app/notebookFormat";
import { get, getPage } from "../../shared/utils/requestWrapper";

export const dateFormat = 'dd-MM-yyyy';
export const dateTimeFormat = 'dd-MM-yyyy HH:mm';

export interface Subscription {
    readonly id: string;
    readonly subscriptionStart: string;
    readonly subscriptionEnd: string;
    readonly currentBill: string;
    readonly details: SubscriptionDetails;
    readonly company: string;
    readonly contactPerson: string;
    readonly contactPersonEmail: string;
    readonly contactPersonPhone: string;
    readonly address1: string;
    readonly address2?: string;
    readonly zip: string;
    readonly city: string;
    readonly country: string;
}

export interface SubscriptionDto extends Subscription {
    readonly projectName: string;
}

export interface SubscriptionDetails {
    readonly subscriptionTier: Tier;
    readonly name: string;
    readonly billingPeriod: BillingPeriodicity;
    readonly subscriptionFee: number;
    readonly includedCredits: number;
    readonly pricePerAdditionalCredit: number;
    readonly numberOfEnvironments: number;
}

export type Tier = 'Basic' | 'Pro' | 'Enterprise';
export type BillingPeriodicity = 'Monthly' | 'Yearly';

export interface Bill {
    readonly id: string;
    readonly periodStart: string;
    readonly periodEnd: string;
    readonly billDate: string;
    readonly subscription: Subscription;
    readonly status: BillingStatus;
    readonly totalAmount: number;
    readonly totalCreditsUsed: number;
    readonly extraCredits?: number;
    readonly extraCreditAmount?: number;
}

export type BillingStatus = 'Unpaid' | 'Paid';

export namespace BillingApi {
    export async function getSubscription(projectId: string) {
        const response = await get<Subscription>(`/api/subscriptions/${projectId}`);
        return response.data;
        // return {
        //     ...response.data,
        //     ...subscriptionMock
        // } as Subscription;
    }

    export async function getBills(projectId: string, page: number, pageSize: number) {
        const params = {page, pageSize};
        const response = await get<Bill[]>(`/api/subscriptions/${projectId}/bills`, {params});
        return [response.data, parseInt(response.headers['x-total-count'])] as const;
        // return [response.data.map(b => ({...b, subscription: {...b.subscription, ...subscriptionMock}})), parseInt(response.headers['x-total-count'])] as const;
    }

    export async function getBill(projectId: string, id: string) {
        const response = await get<Bill>(`/api/subscriptions/${projectId}/bills/${id}`);
        return response.data;
        // return {
        //     ...response.data,
        //     subscription: {...response.data.subscription, ...subscriptionMock},
        //     status: "Paid"
        // } as Bill;
    }

    export function getSessions(projectId: string, billId: string, page: number, pageSize: number) {
        const params = {page, pageSize};
        return getPage<SessionDescriptor>(`/api/subscriptions/${projectId}/bills/${billId}/sessions`, {params});
    }
}

// const subscriptionMock: Partial<Subscription> = {
//     subscriptionEnd: null,
//     company: 'Walde Immobilien AG',
//     address1: 'Habsburgerstrasse 40',
//     zip: '6003',
//     city: 'Lucerne',
//     country: 'Switzerland',
//     contactPerson: 'Walter White',
//     contactPersonPhone: '+41 41 227 30 30',
//     contactPersonEmail: 'walter.white@walde.ch'
// }
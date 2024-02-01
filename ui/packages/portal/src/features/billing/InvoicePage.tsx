import { useParams } from "react-router-dom";
import { Bill, BillingApi, dateFormat, Subscription } from "./billingApi";
import { useEffect, useState } from "react";
import { BillingAddress } from "./BillingAddress";
import { BillingSessions } from "./BillingSessions";
import { SessionDescriptor } from "../../app/notebookFormat";
import classNames from "classnames";
import { formatDate } from "@open-smc/utils/formatDate";
import { formatAmount, formatCredits } from "./billingUtils";

import styles from "./invoice-page.module.scss";
import credits from "./credits.module.scss";
import fee from "./fee.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import { Page } from "../../shared/utils/requestWrapper";

type InvoicePageParams = {
    projectId: string;
    invoiceId: string;
}

export function InvoicePage() {
    const {projectId, invoiceId} = useParams<InvoicePageParams>();
    const [bill, setBill] = useState<Bill>();
    const [sessions, setSessions] = useState<Page<SessionDescriptor>>();

    useEffect(() => {
        (async function () {
            const bill = await BillingApi.getBill(projectId, invoiceId);
            const sessions = await BillingApi.getSessions(projectId, invoiceId, 0, 1000);
            setBill(bill);
            setSessions(sessions);
        })();
    }, [projectId, invoiceId]);

    if (!bill || !sessions) {
        return <div className={loader.loading}>Loading...</div>;
    }

    const {subscription, totalCreditsUsed, extraCredits, extraCreditAmount, totalAmount, status} = bill;
    const {subscriptionFee, billingPeriod, includedCredits} = subscription.details;

    const totalClassname = classNames({
        paid: status === 'Paid',
        unpaid: status === 'Unpaid'
    });

    return (
        <div className={classNames(styles.main, styles.invoice, "invoice")}>
            <div className={styles.container}>
                <div className={styles.content}>
                    <div className={styles.header}>
                        <div className={styles.column}>
                            <img className={styles.logo} src="/logo-systemorph.png" alt="logo-systemorph" width={266}
                                 height={84}/>
                            <InvoiceSubscription bill={bill} subscription={subscription}/>
                        </div>
                        <BillingAddress subscription={subscription} printLayout={true}/>
                    </div>
                    <BillingSessions bill={bill} sessions={sessions} printLayout={true}/>
                    <table className={classNames(fee.table, bill.status === 'Paid' ? 'paid' : 'unpaid')}>
                        <tbody>
                        <tr className={fee.row}>
                            <td className={fee.cell}>{billingPeriod === 'Yearly' ? 'Yearly fee' : 'Monthly fee'}</td>
                            <td className={fee.cell}>{formatAmount(subscriptionFee)}</td>
                        </tr>
                        <tr className={fee.row}>
                            <td className={fee.cell}>Extra credits, CHF</td>
                            <td className={fee.cell}>{formatAmount(extraCreditAmount)}</td>
                        </tr>
                        <tr className={fee.row}>
                            <td className={classNames(fee.cell, {totalClassname})}>
                                <b>Total</b>
                            </td>
                            <td className={fee.cell}>
                                <b>
                                    {formatAmount(totalAmount)}
                                </b>
                            </td>
                        </tr>
                        </tbody>
                    </table>
                </div>
            </div>

        </div>
    );
}

interface InvoiceSubscriptionProps {
    bill: Bill;
    subscription: Subscription;
}

function InvoiceSubscription({bill, subscription}: InvoiceSubscriptionProps) {
    const {periodStart, periodEnd} = bill;
    const {id} = subscription;
    const {subscriptionFee, pricePerAdditionalCredit, includedCredits, billingPeriod} = subscription.details;

    return (
        <div className={styles.subscription}>
            <div>
                <div className={styles.label}>Subscription ID:
                    <span className={styles.value}> {id}</span>
                </div>
                <div className={styles.label}>Billing details:
                    <span className={styles.value}> {bill.id}</span>
                </div>
            </div>
            <div className={styles.row}>
                <div className={styles.date}>
                    {formatDate(periodStart, dateFormat)} to {formatDate(periodEnd, dateFormat)}
                </div>
                <ul className={credits.list}>
                    <li className={credits.item}>
                        <label className={credits.label}>Credits</label>
                        <span className={credits.value}>{formatCredits(includedCredits)}</span>
                    </li>
                    <li className={credits.item}>
                        <label
                            className={credits.label}>{billingPeriod === 'Yearly' ? 'Yearly fee' : 'Monthly fee'}</label>
                        <span className={credits.value}>{formatAmount(subscriptionFee)}</span>
                    </li>
                    <li className={credits.item}>
                        <label className={credits.label}>Extra credits</label>
                        <span className={credits.value}>{formatAmount(pricePerAdditionalCredit)} <small
                            className={credits.valueSmall}> CHF / credit</small></span>
                    </li>
                </ul>
            </div>
        </div>
    )
}
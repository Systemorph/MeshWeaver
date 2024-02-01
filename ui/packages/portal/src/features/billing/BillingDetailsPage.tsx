import { Link, useParams } from "react-router-dom";
import { Bill, BillingApi, dateFormat } from "./billingApi";
import { useEffect, useState } from "react";
import { useProject } from "../project/projectStore/hooks/useProject";
import { SubscriptionDetails } from "./SubscriptionDetails";
import { BillingAddress } from "./BillingAddress";
import { BillingSessions } from "./BillingSessions";
import { SessionDescriptor } from "../../app/notebookFormat";
import classNames from "classnames";
import Tooltip from "rc-tooltip";
import { formatAmount, formatFee } from "./billingUtils";
import styles from "./billing-details.module.scss";
import fee from "./fee.module.scss";
import "@open-smc/ui-kit/components/tooltip.scss";
import { formatDate } from "@open-smc/utils/numbers";
import { Page } from "../../shared/utils/requestWrapper";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

export type BillingDetailsPageParams = {
    billId: string;
}

export function BillingDetailsPage() {
    const {project} = useProject();
    const {billId} = useParams<BillingDetailsPageParams>();
    const [bill, setBill] = useState<Bill>();
    const [sessions, setSessions] = useState<Page<SessionDescriptor>>();

    useEffect(() => {
        (async function () {
            const bill = await BillingApi.getBill(project.id, billId);
            const sessions = await BillingApi.getSessions(project.id, billId, 0, 1000);
            setBill(bill);
            setSessions(sessions);
        })();
    }, []);

    if (!bill || !sessions) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <BillingDetails bill={bill} sessions={sessions}/>
    )
}

interface BillingDetailsProps {
    bill: Bill;
    sessions: Page<SessionDescriptor>;
}

function BillingDetails({bill, sessions}: BillingDetailsProps) {
    const {project} = useProject();
    const {id, subscription, extraCreditAmount, totalAmount, status, periodStart, periodEnd} = bill;
    const {subscriptionFee, billingPeriod, pricePerAdditionalCredit} = subscription.details;

    const totalClassname = classNames({
        paid: status === 'Paid',
        unpaid: status === 'Unpaid'
    });

    const extraCreditsInfo = (
        <Tooltip overlayClassName={"tooltip"} placement={'top'}
                 overlay={<span>Price per credit<br/> {pricePerAdditionalCredit} CHF</span>}>
            <i className={classNames(fee.icon, 'sm sm-info')}/>
        </Tooltip>
    );

    return (
        <div className={styles.billingContainer}>
            <h3 className={styles.title}>
                <Link to='..'>Overview</Link> / <span className={styles.subtitle}>Billing details</span>
            </h3>
            <div className={styles.container}>
                <SubscriptionDetails subscription={subscription}/>
                <BillingAddress subscription={subscription}/>
            </div>

            <h5 className={styles.details}>
                <span className={styles.id}><b>Billing details:</b> <span>{id}</span></span>
                <span
                    className={styles.period}><b>Billing period:</b> <span>{formatDate(periodStart, dateFormat)} to {formatDate(periodEnd, dateFormat)}</span></span>
                <Link className={styles.link} to={`/project/${project.id}/invoice/${id}`}>Invoice</Link>
            </h5>
            <BillingSessions bill={bill} sessions={sessions}/>

            <table className={classNames(fee.table, bill.status === 'Paid' ? 'paid' : 'unpaid')}>
                <tbody>
                <tr className={fee.row}>
                    <td className={fee.cell}>{billingPeriod === 'Yearly' ? 'Yearly fee' : 'Monthly fee'}</td>
                    <td className={fee.cell}>{formatFee(subscriptionFee)}</td>
                </tr>
                <tr className={fee.row}>
                    <td className={fee.cell}>Extra credits, CHF {extraCreditsInfo}</td>
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
    );
}
import { formatDate } from "@open-smc/utils/formatDate";
import { dateFormat, Subscription } from "./billingApi";
import { formatAmount, formatCredits, formatFee } from "./billingUtils";

import styles from "./subscribtion-details.module.scss";
import credits from "./credits.module.scss";
import classNames from "classnames";

interface SubscriptionDetailsProps {
    subscription: Subscription;
}

export function SubscriptionDetails({subscription}: SubscriptionDetailsProps) {
    const {id, subscriptionEnd, subscriptionStart} = subscription;
    const isActive = !subscriptionEnd;
    const {subscriptionFee, pricePerAdditionalCredit, includedCredits, billingPeriod} = subscription.details;

    return (
        <div className={classNames(styles.container, isActive ? styles.active : '')}>
            <div className={styles.header}>
                <h3 className={classNames(styles.title, isActive ? styles.active : '')}>{isActive ? "Active subscription" : "Subscription"}</h3>
                <span className={styles.id}>ID: {id}</span>
            </div>

            {isActive
                ? <h5 className={styles.start}>Started on {formatDate(subscriptionStart, "MMMM dd, yyyy")}</h5>
                : <h5 className={styles.start}>{formatDate(subscriptionStart, dateFormat)} to {formatDate(subscriptionEnd, dateFormat)}</h5>
            }
            <ul className={credits.list}>
                <li className={credits.item}>
                    <label className={credits.label}>Credits</label>
                    <span className={credits.value}>{formatCredits(includedCredits)}</span>
                </li>
                <li className={credits.item}>
                    <label className={credits.label}>{billingPeriod === 'Yearly' ? 'Yearly fee' : 'Monthly fee'}</label>
                    <span className={credits.value}>{formatFee(subscriptionFee)}</span>
                </li>
                <li className={credits.item}>
                    <label className={credits.label}>Extra credits</label>
                    <span className={credits.value}>{formatAmount(pricePerAdditionalCredit)}
                        <small className={credits.valueSmall}> CHF / credit</small></span>
                </li>
            </ul>
        </div>
    )
}
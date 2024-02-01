import { useEffect, useState } from "react";
import { useProject } from "../project/projectStore/hooks/useProject";
import { SubscriptionDetails } from "./SubscriptionDetails";
import { BillingAddress } from "./BillingAddress";
import { BillingHistory } from "./BillingHistory";
import { Bill, BillingApi, Subscription } from "./billingApi";
import { Pagination } from "../../shared/components/paginator/Pagination";
import { usePaginationParams } from "../../shared/hooks/usePaginationParams";
import styles from "./billing-overview.module.scss";
import { useThrowAsync } from "@open-smc/utils/useThrowAsync";
import loader from "@open-smc/ui-kit/components/loader.module.scss";

const PAGE_SIZE = 10;

export function BillingOverviewPage() {
    const {project} = useProject();
    const [subscription, setSubscription] = useState<Subscription>();
    const [bills, setBills] = useState<Bill[]>();
    const [totalCount, setTotalCount] = useState<number>();
    const [loading, setLoading] = useState(true);
    const [page, setPage] = usePaginationParams();
    const throwAsync = useThrowAsync();

    useEffect(() => {
        (async function () {
            try {
                const subscription = await BillingApi.getSubscription(project.id);
                setSubscription(subscription);
                const [bills, totalCount] = await BillingApi.getBills(project.id, page - 1, PAGE_SIZE);
                setBills(bills);
                setTotalCount(totalCount);
                setLoading(false);
            } catch (error) {
                throwAsync(error);
            }
        })();
    }, [page]);

    if (loading) {
        return (
            <div className={loader.loading}>Loading...</div>
        );
    }

    return (
        <div>
            <div className={styles.container}>
                <SubscriptionDetails subscription={subscription}/>
                <BillingAddress subscription={subscription}/>
            </div>
            <h3 className={styles.title}>Billing history</h3>
            <BillingHistory bills={bills}/>
            <p className={styles.text}>All amounts shown are in CHF.</p>
            <Pagination
                totalCount={totalCount}
                page={page}
                pageSize={PAGE_SIZE}
                onPageChanged={setPage}/>
        </div>
    );
}
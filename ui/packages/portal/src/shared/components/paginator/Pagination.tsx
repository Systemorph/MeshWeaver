import {
    Paginator,
    PaginatorNextPageLinkOptions,
    PaginatorPageLinksOptions,
    PaginatorPageState,
    PaginatorPrevPageLinkOptions,
} from "primereact/paginator";
import styles from "./pagination.module.scss";
import classNames from "classnames";
import { Button } from "@open-smc/ui-kit/components/Button";

type Props = {
    page: number;
    totalCount: number;
    pagesToShow?: number;
    pageSize: number;
    onPageChanged: (newPage: number) => void
};

const pagesAmount = (totalCount: number, pageSize: number) => totalCount && Math.ceil(totalCount / pageSize)

export function Pagination({page, totalCount, pagesToShow = 5, pageSize, onPageChanged}: Props) {
    const onPageChange = (event: PaginatorPageState) => {
        onPageChanged(event.page + 1);
    };

    const template = {
        layout: 'PrevPageLink PageLinks NextPageLink',
        'PrevPageLink': (options: PaginatorPrevPageLinkOptions) => {
            return (page !== 1) &&
            <Button type="button" className={classNames(styles.prev, options.className)}
                    onClick={options.onClick}
                    disabled={options.disabled} data-qa-btn-prev>
                <i className="sm sm-chevron-left"/>
                <span className="p-3">Prev</span>
            </Button>
        },
        'PageLinks': (options: PaginatorPageLinksOptions) => (
            <Button type="button"
                    className={classNames(styles.page, options.className )}
                    onClick={(event) => (options.currentPage !== options.page && options.onClick(event))} data-qa-btn-page={options.page + 1}>
                {options.page + 1}
            </Button>
        ),
        'NextPageLink': (options: PaginatorNextPageLinkOptions) => {
            const isLast = page === pagesAmount(totalCount, pageSize);

            return (
                !isLast &&
                <Button type="button" className={classNames(styles.next, options.className)} onClick={options.onClick}
                        disabled={options.disabled} data-qa-btn-next>
                    <span className="p-3">Next</span>
                    <i className="sm sm-chevron-right"/>
                </Button>
            )
        }
    };

    return totalCount > pageSize && <div data-qa-pagination>
        <Paginator template={template}
                   rows={pageSize}
                   totalRecords={totalCount}
                   first={(page-1) * pageSize}
                   pageLinkSize={pagesToShow}
                   onPageChange={onPageChange}
                   className={styles.paginator}/></div>
}
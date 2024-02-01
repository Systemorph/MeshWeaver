import { useSearchParamsUpdate } from "./useSearchParamsUpdate";
import { isInteger } from "lodash";

const errorMessage = 'Page number should be a positive integer';
export const PAGE_PARAM = 'page';
const FIRST_PAGE = 1;

const getPage = (value: string) => {
    if(!isInteger(+value) || +value < FIRST_PAGE) {
        return FIRST_PAGE;
    }

    return parseInt(value) || 1;
}

export function usePaginationParams(): [number, (page: number) => void| undefined] {
    const [searchParams, setSearchParams] = useSearchParamsUpdate();
    const page = getPage(searchParams.get(PAGE_PARAM));

    return [
        page,
        (page) => {
            setSearchParams([{
                key: PAGE_PARAM,
                value: page
            }])
        }
    ]
}
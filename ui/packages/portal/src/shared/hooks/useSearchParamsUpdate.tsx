import { useSearchParams } from "react-router-dom";
import { isNil } from "lodash";

export const RECENT_TYPE_PARAM = 'rt';
export const SEARCH_PARAM = 'search';

type ParamsTuple = { key: string, value?: string | number };

export const getSearchParams = (params: ParamsTuple[]) => {
    return params.reduce((acc, {key, value}) => {
        !isNil(value) ? acc.set(key, value + '') : acc.delete(key);
        return acc;
    }, new URLSearchParams());
}

export function useSearchParamsUpdate(): [URLSearchParams, (params: ParamsTuple[]) => void] {
    const [params, setParams] = useSearchParams();

    const updater = (params: ParamsTuple[]) => {
        setParams((sp) => {
            params.forEach(({key, value}) => {
                !isNil(value) ? sp.set(key, value + '') : sp.delete(key);
            })

            return sp;
        });
    }

    return [params, updater];
}
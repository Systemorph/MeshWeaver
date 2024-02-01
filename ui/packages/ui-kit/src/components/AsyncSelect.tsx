import {useCallback, useEffect, useRef, useState} from "react";
import {debounce} from "lodash";
import {Select, SelectProps} from "./Select";
import { ProgressSpinner } from "./ProgressSpinner";

interface AsyncSelectProps<TValue> extends Omit<SelectProps<TValue>, 'options'> {
    searchEnabled?: boolean;
    getOptions: (search?: string) => Promise<TValue[]>;
}

export function AsyncSelect<TValue>({getOptions, searchEnabled, ...selectProps}: AsyncSelectProps<TValue>) {
    const [loading, setLoading] = useState(false);
    const [options, setOptions] = useState<TValue[]>();
    const [dropdownOpen, setDropdownOpen] = useState(false);
    const [search, setSearch] = useState<string>();
    const getOptionsAttemptRef = useRef(0);

    const loadOptions = useCallback(async (search?: string) => {
        const attempt = ++getOptionsAttemptRef.current;
        setLoading(true);

        const options = await getOptions(search);

        if (attempt === getOptionsAttemptRef.current) {
            setOptions(options);
            setLoading(false);
        }
    }, [getOptions, getOptionsAttemptRef]);

    const loadOptionsDebounced = useCallback(debounce(loadOptions, 500), [loadOptions]);

    useEffect(() => {
        if (dropdownOpen) {
            setOptions([]);
            loadOptions();
        }
    }, [dropdownOpen]);

    useEffect(() => {
        if (dropdownOpen) {
            setLoading(true);
            getOptionsAttemptRef.current++;
            loadOptionsDebounced(search);
        }
    }, [search]);

    return (
        <Select
            {...selectProps}
            showSearch={searchEnabled}
            onSearch={searchEnabled ? setSearch : null}
            filterOption={false}
            inputIcon={loading ? <ProgressSpinner style={{width: '16px', height: '16px'}}/> : <i className={"sm sm-chevron-down"}/>}
            onDropdownVisibleChange={setDropdownOpen}
            options={options}
            notFoundContent={loading ? null : <div>Not found</div>}
        />
    );
}
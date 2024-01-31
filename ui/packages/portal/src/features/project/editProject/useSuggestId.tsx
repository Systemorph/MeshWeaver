import { UseFormReturn } from "react-hook-form";
import { ProjectApi, ProjectSettings } from "../../../app/projectApi";
import { useCallback, useEffect, useState } from "react";
import { debounce, isEmpty } from "lodash";

export function useSuggestId(form: UseFormReturn<ProjectSettings>, autoSuggestByName: boolean) {
    const {getValues, watch, setValue, clearErrors} = form;
    const projectName = watch('name');
    const [watchEnabled, setWatchEnabled] = useState(false);
    const [isLoading, setIsLoading] = useState(false);

    const suggestId = useCallback(() => {
        const name = getValues('name');
        setIsLoading(true);

        ProjectApi.suggestId(name).then(newId => {
            if (name === getValues('name')) {
                setValue('id', newId, {shouldValidate: false});
                clearErrors('id');
                setIsLoading(false);
            }
        });
    }, []);

    const suggestIdDebounced = useCallback(debounce(suggestId, 300), []);

    useEffect(() => {
        setWatchEnabled(true);

        if (watchEnabled && autoSuggestByName) {
            if (isEmpty(projectName)) {
                suggestIdDebounced.cancel();
                setValue('id', '', {shouldValidate: true});
                setIsLoading(false);
            } else {
                setIsLoading(true);
                suggestIdDebounced();
            }
        }
    }, [projectName]);

    return {suggestId, isLoading};
}
import styles from './setupProject.module.scss';
import { Controller, UseFormReturn } from 'react-hook-form';
import { useCallback, useState } from "react";
import classNames from "classnames";
import pDebounce from 'p-debounce';
import { isEmpty } from "lodash";
import { ProjectApi, ProjectSettings } from "../../../app/projectApi";
import { Button } from "@open-smc/ui-kit/components/Button";
import { InputText } from "@open-smc/ui-kit/components/InputText";
import button from "@open-smc/ui-kit/components/buttons.module.scss"

type Props = {
    form: UseFormReturn<ProjectSettings>,
    isLoading: boolean;
    onRefresh: () => void;
};

export function IdEditorField({form, onRefresh, isLoading}: Props) {
    const {control, getValues, formState, clearErrors} = form;
    const {errors, dirtyFields, isValidating: formIsValidating} = formState;
    const [isValidating, setIsValidating] = useState(false);

    const asyncValidationDebounced = useCallback(pDebounce(async (id: string) => {
        if (id === getValues('id')) {
            setIsValidating(true);
            const messages = await ProjectApi.validateId(id);

            if (id === getValues('id')) {
                setIsValidating(false);
                return isEmpty(messages) || messages.join(' ');
            }
        }
        return false;
    }, 300), []);

    const validate = (async (id: string) => {
        if (isEmpty(id)) {
            if (isValidating) {
                setIsValidating(false);
            }
            return 'Project ID is required';
        }

        clearErrors('id');

        return asyncValidationDebounced(id);
    });

    return (
        <div className={classNames(styles.field, styles.fieldHorizontal, styles.editMode)} data-qa-project-id>
            <label className={styles.label} htmlFor="id">ID</label>
            <Controller name="id"
                        defaultValue=""
                        control={control}
                        rules={{
                            validate
                        }}
                        render={({field, fieldState}) => {
                            return (
                                <div className={styles.idBox}>
                                    <InputText
                                        id={field.name}
                                        {...field}
                                        autoFocus
                                        processing={isValidating || formIsValidating || isLoading}
                                        disabled={isLoading}
                                        check
                                        invalid={fieldState.isDirty && !!fieldState.error}
                                    />

                                    <Button type={'button'}
                                            disabled={formIsValidating || isLoading || !!errors.name}
                                            className={classNames(styles.retry, button.button)}
                                            icon="sm sm-refresh"
                                            onClick={onRefresh} data-qa-btn-retry/>
                                </div>
                            )
                        }}/>
            {dirtyFields.id && errors.id &&
                <small className={classNames(styles.errorMessage, styles.errorMessageId,)}>{errors.id.message}</small>}
        </div>
    );
}
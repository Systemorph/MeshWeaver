import { Controller, useForm } from "react-hook-form";
import { isEmpty } from "lodash";
import classNames from "classnames";
import { EnvApi } from "./envApi";
import { useEffect, useState } from "react";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import styles from "./environments.module.scss";
import button from "@open-smc/ui-kit/src/components/buttons.module.scss";
import { InputText } from '@open-smc/ui-kit/src/components/InputText';

type Props = {
    projectId: string;
    environmentId: string;
    onDeleted?: (env: string) => void;
    onCancel?: () => void;
};

type EnvironmentForm = {
    projectId: string,
    environmentId: string;
    newEnvironmentId?: string
}

export function DeleteEnvironmentForm({projectId, environmentId, onDeleted, onCancel}: Props) {
    const {handleSubmit, formState: {errors}, control, getValues, trigger} = useForm<EnvironmentForm>({
        mode: 'onChange',
        defaultValues: {
            projectId
        },
    });

    const [isLoading, setIsLoading] = useState(false);

    const submit = async (data: EnvironmentForm, event: any) => {
        event.preventDefault();

        setIsLoading(true);
        await EnvApi.deleteEnvironment(projectId, environmentId);
        setIsLoading(false);
        onDeleted && onDeleted(environmentId);
    };

    const disabled = !isEmpty(errors) || isLoading;

    useEffect(() => {
        trigger();
    }, []);

    return (
        <div className={classNames(styles.formContainer, 'delete')}>
            <h2 className={styles.heading}>Delete Environment</h2>
            <form onSubmit={handleSubmit(submit)} className={styles.form} autoComplete="off">
                <span className={styles.label}>Type the name "{environmentId}" to delete environment</span>
                <div className={classNames(styles.idBox)}>
                    <Controller name="environmentId"
                                defaultValue=""
                                control={control}
                                rules={{
                                    validate: (value) => {
                                        return value === environmentId
                                    }
                                }}
                                render={({field}) => (
                                    <div className={styles.idField}>
                                        <InputText
                                            placeholder={environmentId}
                                            autoFocus={true}
                                            id={field.name}
                                            {...field}
                                            processing={isLoading}
                                            check
                                            invalid={!!errors.environmentId || !getValues('environmentId') || getValues('environmentId') === environmentId}
                                        />
                                    </div>)
                                }/>
                </div>
                <div className={styles.buttonsContainer}>
                    <Button
                        className={classNames(button.button, button.primaryButton, button.buttonSmall, button.delete)}
                        type="submit"
                        label="delete"
                        disabled={disabled}/>
                    <Button className={classNames(button.button, button.cancelButton)}
                            type="button"
                            icon="sm sm-close"
                            label="Cancel"
                            onClick={() => onCancel()}/>
                </div>
            </form>
        </div>
    );
}
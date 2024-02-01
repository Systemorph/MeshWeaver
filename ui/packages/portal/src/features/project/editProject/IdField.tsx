import styles from './setupProject.module.scss';
import { Controller, UseFormReturn } from 'react-hook-form';
import classNames from "classnames";
import { ProjectSettings } from "../../../app/projectApi";
import { Button } from "@open-smc/ui-kit/components/Button";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import { ProgressSpinner } from '@open-smc/ui-kit/components/ProgressSpinner';

type Props = {
    form: UseFormReturn<ProjectSettings>,
    isLoading?: boolean,
    onEdit?: () => void
};

export function IdField({form, onEdit, isLoading}: Props) {
    const {control, formState} = form;
    const {errors, dirtyFields} = formState;

    return (
        <div className={classNames(styles.field, styles.fieldHorizontal, styles.readMode)} data-qa-project-id>
            <label className={styles.label} htmlFor="id">ID:</label>
            <Controller name="id"
                        control={control}
                        render={({field, fieldState}) => {
                            return (
                                <div className={styles.idWrapper}>
                                    <span className={styles.fieldText} data-qa-project-id-text>
                                        {!!field.value ? field.value : 'Please, specify project name first'}
                                    </span>
                                    {isLoading && <ProgressSpinner style={{width: '16px', height: '16px'}}/>}
                                    {!isLoading && !!field.value && !!onEdit && <Button type='button'
                                                                                        className={classNames(styles.editButton, button.button)}
                                                                                        label="Edit"
                                                                                        onClick={onEdit}
                                                                                        data-qa-btn-edit/>}
                                </div>
                            )
                        }}/>
            {dirtyFields.id && errors.id && <small className={styles.error}>{errors.id.message}</small>}
        </div>
    );
}
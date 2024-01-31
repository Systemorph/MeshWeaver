import styles from './setupProject.module.scss';
import { Controller, UseFormReturn } from 'react-hook-form';
import { ProjectSettings } from "../../../app/projectApi";
import { InputText } from "@open-smc/ui-kit/components/InputText";

type Props = {
    form: UseFormReturn<ProjectSettings>;
    disabled?: boolean;
}

export function NameField({form, disabled}: Props) {
    const {control} = form;

    return (
        <div className={styles.field} data-qa-field-name>
            <label className={styles.label} htmlFor="name">Project name</label>
            <Controller name="name"
                        defaultValue=""
                        control={control}
                        rules={{
                            required: 'Project name is required'
                        }}
                        render={({field, fieldState}) => {
                            return (
                                <>
                                    <InputText
                                        id={field.name}
                                        autoFocus
                                        {...field}
                                        invalid={fieldState.isDirty && !!fieldState.error}
                                        disabled={disabled}
                                    />
                                    {fieldState.isDirty && fieldState.error &&
                                        <small className={styles.errorMessage}>{fieldState.error.message}</small>}
                                </>
                            )
                        }}/>
        </div>
    );
}
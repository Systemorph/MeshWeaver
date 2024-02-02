import { Controller, UseFormReturn } from 'react-hook-form';
import { ProjectSettings } from "../../../app/projectApi";
import styles from "./setupProject.module.scss";
import { InputText } from '@open-smc/ui-kit/src/components/InputText';

export function DefaultEnvField({form: {control, formState}}: {form: UseFormReturn<ProjectSettings>}) {
    const {errors} = formState;

    return (
        <div className={styles.field} data-qa-field-env>
            <label className={styles.label} htmlFor="environment">Default environment name</label>
            <Controller name="environment"
                        defaultValue="dev"
                        control={control}
                        rules={{
                            required: 'Default environment is required',
                        }}
                        render={({field, fieldState}) => {
                            return <InputText
                                id={field.name}
                                {...field}
                                invalid={fieldState.isDirty && !!fieldState.error}
                            />
                        }}/>
            {errors.environment &&
                <small className={styles.errorMessage}>{errors.environment.message}</small>}
        </div>

    );
}
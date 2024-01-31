import styles from './setupProject.module.scss';

import { Controller, UseFormReturn } from 'react-hook-form';
import { ProjectSettings } from "../../../app/projectApi";
import { Select } from '@open-smc/ui-kit/components/Select';

type Props = {
    form: UseFormReturn<ProjectSettings>,
    environments: string[]
};

export function EnvironmentField({form, environments}: Props) {
    const {control} = form;

    return (
        <div className={styles.field} data-qa-field-env>
            <label className={styles.label} htmlFor="environment">Environment</label>
            <Controller name="environment"
                        control={control}
                        render={({field}) => (
                            <Select id={field.name}
                                      value={field.value}
                                      onSelect={(value) => field.onChange(value)}
                                      options={environments}
                            />
                        )}/>
        </div>

    );
}
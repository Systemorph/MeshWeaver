import {Controller, UseFormReturn} from 'react-hook-form';
import {useEffect, useState} from "react";
import {find, keyBy} from "lodash";
import {ProjectApi, ProjectSettings, Region} from "../../../app/projectApi";
import styles from './setupProject.module.scss';
import { Select } from '@open-smc/ui-kit/components/Select';

export function HomeRegionField({form: {setValue, control, resetField}}: { form: UseFormReturn<ProjectSettings> }) {
    const [regions, setRegions] = useState<Region[]>();
    useEffect(() => {
        (async function () {
            const regions = await ProjectApi.getRegions();
            setRegions(regions);
            const defaultHomeRegion = find(regions, 'isDefault');
            // TODO: temp solution, pressing cancel should close side menu instead of hiding it
            // once this is possible #24432 this can be removed (8/2/2022, akravets)
            resetField('homeRegion', {defaultValue: defaultHomeRegion?.systemName});
            setValue('homeRegion', defaultHomeRegion?.systemName);
        })();
    }, []);

    if(!regions) {
        return null;
    }
    const regionDict = keyBy(regions, 'displayName');

    return (
        <div className={styles.field} data-qa-field-location>
            <label className={styles.label} htmlFor="homeRegion">Location</label>
            <Controller name="homeRegion"
                        defaultValue={''}
                        control={control}
                        render={({field}) => (
                            <Select id={field.name}
                                      value={field.value}
                                      onSelect={(value) => field.onChange(value)}
                                      options={regions.map(region => region.systemName)}
                                      nameBinding={(value) => regionDict[value].displayName}
                            />
                        )}/>
        </div>

    );
}
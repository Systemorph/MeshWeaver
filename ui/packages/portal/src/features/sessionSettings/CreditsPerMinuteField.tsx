import settings from "./session-settings.module.scss";
import { find, round } from "lodash";
import * as React from "react";
import { SessionTierSpecificationDto } from "../project/projectSessionApi";
import { FormFieldProps } from "./SessionSettingsForm";
import { TierFormModel } from "./TierField";
import { useWatch } from "react-hook-form";

interface CreditsPerMinuteFieldProps extends FormFieldProps<TierFormModel> {
    tiers: SessionTierSpecificationDto[];
}

export function CreditsPerMinuteField({form: {control}, tiers}: CreditsPerMinuteFieldProps) {
    const [tier, cpu] = useWatch({control, name: ['tier', 'cpu']});

    const {creditsPerMinute} = find(tiers, t => t.systemName === tier);

    return (
        <span className={settings.creditsPerMinute}>
            <span className={settings.creditsPerMinuteValue}>
                {round(creditsPerMinute * cpu, 2)}
            </span>
            credits per minute
        </span>
    )
}
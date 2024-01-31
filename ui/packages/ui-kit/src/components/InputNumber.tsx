import * as RcInputNumber from 'rc-input-number';
import { InputNumberProps } from "rc-input-number";
import classNames from "classnames";
import { wrapInput } from "./wrapInput";

export const InputNumber = wrapInput<InputNumberProps>(
    ({className, ...props}, inputClassNames) =>
        <RcInputNumber.default {...props}
                     className={classNames(className, inputClassNames)}/>
);
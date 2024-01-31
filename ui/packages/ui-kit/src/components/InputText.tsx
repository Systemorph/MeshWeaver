import Input from 'rc-input';
import { InputProps } from "rc-input";
import classNames from "classnames";
import { wrapInput } from "./wrapInput";

export const InputText = wrapInput<InputProps>(
    ({className, ...props}, inputClassNames) => <Input {...props}
                                                           className={classNames(className, inputClassNames)}/>
);
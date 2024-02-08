import { Builder } from "@open-smc/utils/src/Builder";
import { DataContext, DataContextChangeHandler } from "./DataContext";

export class DataContextBuilder extends Builder<DataContext> {
    constructor(value: unknown) {
        super(DataContext);
        this.withValue(value);
    }

    withParentContext(value: DataContext) {
        this.data.parentContext = value;
        return this;
    }

    withOnChange(value: DataContextChangeHandler) {
        this.data.onChange = value;
        return this;
    }

    withValue(value: unknown) {
        this.data.value = value;
        return this;
    }
}

export const makeDataContext = (value: unknown) => new DataContextBuilder(value);
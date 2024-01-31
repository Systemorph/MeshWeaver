import * as dateFns from "date-fns";

export function formatDate(date: string, format: string) {
    return dateFns.format(new Date(date), format);
}
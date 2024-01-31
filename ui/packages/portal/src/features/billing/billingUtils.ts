import { formatNumber } from "@open-smc/utils/numbers";

export function formatAmount(amount: number) {
    return formatNumber(amount, '# ##0.00');
}

export function formatFee(amount: number) {
    return `${formatNumber(amount, '# ##0.')}.-`;
}

export function formatCredits(credits: number) {
    return formatNumber(credits, '# ##0.');
}
import format from 'number-format.js';

var signDigitsDecimalPadStr = Array(50).join('#');

/*
Examples of standard formatting provided by number-format.js: http://mottie.github.io/javascript-number-formatter/

Additional formatting implemented on top of number-format.js:

percentages:
    formatNumber(0.95, '0.00%') => '95.00%'
    formatNumber(0.123, '0.00%') => '12.30%'

significant digits:
    formatNumber(12345.67, '# ##0.##!') => '12 346'
    formatNumber(1.19999, '0.##!') => '1.2'
    formatNumber(0.126, '0.##!') => '0.13'
    formatNumber(0.000459, '0.##!') => '0.00046'
*/
export function formatNumber(value: number, mask: string, converter?: (value: number, isPercentage: boolean) => number): string {
    // find prefix/suffix
    var len = mask.length,
        start = mask.search(/[0-9\-\+#]/),
        prefix = start > 0 ? mask.substring(0, start) : '',
        // reverse string: not an ideal method if there are surrogate pairs
        str = mask.split('').reverse().join(''),
        end = str.search(/[0-9\-\+#]/),
        offset = len - end,
        substr = mask.substring(offset, offset + 1),
        indx = offset + ((substr === '.' || (substr === ',')) ? 1 : 0),
        suffix = end > 0 ? mask.substring(indx, len) : '';

    // mask with prefix & suffix removed
    var cleanMask = mask.substring(start, indx);

    // percentages
    const isPercentage = suffix.search(/^\s*%/) !== -1;
    if (isPercentage && value !== null && value !== undefined) {
        value = value * 100;
    }

    if (converter) value = converter(value, isPercentage);

    // rounding to significant digits
    if (suffix[0] === '!') {
        suffix = suffix.substr(1);
        var reversedMask = str.substring(end, len - start),
            decimalsReversedIndex = reversedMask.search(/[^0-9\-\+#]/);
        if (decimalsReversedIndex !== -1) {
            var decimalsIndex = cleanMask.length - decimalsReversedIndex,
                wholeMask = cleanMask.substring(0, decimalsIndex),
                decimalsMask = cleanMask.substr(decimalsIndex),
                signDigitsNum = decimalsMask.length,
                [wholePart, decimalPart] = value.toString().split('.');

            if (wholePart[0] === '-') wholePart = wholePart.substr(1);

            var wholeDigits = wholePart !== '0' ? wholePart.length : 0;

            if (wholeDigits > 0) {
                decimalsMask = decimalsMask.substr(wholeDigits);
            }
            else if (decimalPart) {
                var padLength = decimalPart.search(/[1-9]/);
                if (padLength !== -1) {
                    decimalsMask = signDigitsDecimalPadStr.substr(0, padLength) + decimalsMask;
                }
            }

            cleanMask = wholeMask + decimalsMask;
        }
        else {
            throw `Wrong format string ${ mask }, significant digits should follow decimal separator.`;
        }
    }

    return format(prefix + cleanMask + suffix, value);
}

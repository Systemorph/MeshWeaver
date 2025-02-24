declare const MathJax: any;

function typeset() {
    if (typeof MathJax === "undefined") {
        console.warn("MathJax not loaded");
        return Promise.resolve();
    }
    if (typeof MathJax.typesetPromise !== "function") {
        console.warn("MathJax.typesetPromise is not a function");
        return Promise.resolve();
    }
    return MathJax.typesetPromise();
}

function waitForMathJax() {
    return new Promise((resolve) => {
        if (typeof MathJax !== "undefined" && MathJax.startup && MathJax.startup.promise) {
            MathJax.startup.promise.then(() => resolve(true));
        } else {
            resolve(false);
        }
    });
}

export {
    typeset,
    waitForMathJax
};
import { c as cloneDeepWith, a as createGrid, i as isString } from "./vendor-CTL2NXIB.mjs";
const gridInstances = /* @__PURE__ */ new Map();
const renderGrid = (id, element, options) => {
  destroyGrid(id);
  const clonedOptions = cloneDeepWith(options, (value) => {
    if (isString(value) && funcRegexps.some((regexp) => regexp.test(value))) {
      try {
        return eval(`(${value})`);
      } catch (error) {
        console.error("Error evaluating function string:", error);
        return null;
      }
    }
  });
  gridInstances.set(id, createGrid(element, clonedOptions));
};
const destroyGrid = (id2) => {
  if (gridInstances.has(id2)) {
    gridInstances.get(id2).destroy();
    gridInstances.delete(id2);
  }
};
const funcRegexps = [
  /^function\b/,
  /^\(function\b/,
  /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];
export {
  destroyGrid,
  renderGrid
};

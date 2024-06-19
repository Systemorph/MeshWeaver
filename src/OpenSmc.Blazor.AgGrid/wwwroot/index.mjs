import { c as createGrid, a as cloneDeepWith, i as isString } from "./vendor-DpIhZ_dQ.mjs";
const instances = /* @__PURE__ */ new Map();
const renderGrid = (id, element, options) => {
  const instance = instances.get(id);
  if (instance) {
    instance.destroy();
    instances.delete(id);
  }
  const gridOptions = deserialize(options);
  instances.set(id, createGrid(element, gridOptions));
};
function deserialize(data) {
  return cloneDeepWith(data, (value) => {
    if (isString(value) && funcRegexps.some((regexp) => regexp.test(value))) {
      try {
        return eval(`(${value})`);
      } catch (error) {
        console.error("Error evaluating function string:", error);
        return null;
      }
    }
  });
}
const funcRegexps = [
  /^function\b/,
  /^\(function\b/,
  /^\s*(\s*[a-zA-Z]\w*|\(\s*[a-zA-Z]\w*(\s*,\s*[a-zA-Z]\w*)*\s*\))\s*=>/
];
export {
  renderGrid
};

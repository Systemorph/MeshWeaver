const path = require('path');
const {babelInclude} = require("customize-cra");
const {name, version} = require("./package.json");

const packageId = `${name}-${version}`;

module.exports = function override(config, env) {
  // config.output.publicPath = 'auto';
  config.optimization.minimize = false;

  config.output.uniqueName = packageId;
  
  babelInclude([
    path.resolve('src'),
    // adding packages folder to babel
    path.resolve('../application/src'),
    path.resolve('../shared/src'),
    path.resolve('../utils/src'),
    // path.resolve('../modularity/src'),
    path.resolve('../store/src'),
    // path.resolve('../notebook/src'),
    path.resolve('../portal/src'),
  ])(config);

  return config;
}
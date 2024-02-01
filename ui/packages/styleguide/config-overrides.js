const path = require('path');
const {babelInclude} = require("customize-cra");
const {name, version} = require("./package.json");
const MonacoWebpackPlugin = require('monaco-editor-webpack-plugin');
const addPackageAliases = require("../../addPackageAliases");

const packageId = `${name}-${version}`;

module.exports = function override(config, env) {
  config.plugins.push(new MonacoWebpackPlugin({
    languages: ['csharp', 'json', 'markdown', "javascript", "typescript"]
  }));

  config.output.publicPath = 'auto';
  config.optimization.minimize = false;

  config.output.uniqueName = packageId;
  
  babelInclude([
    path.resolve('src'),
    // adding packages folder to babel
    path.resolve('../application/src'),
    path.resolve('../portal/src'),
    path.resolve('../sandbox/src'),
    path.resolve('../store/src'),
    path.resolve('../ui-kit/src'),
    path.resolve('../utils/src'),
  ])(config);

  addPackageAliases(config);

  return config;
}
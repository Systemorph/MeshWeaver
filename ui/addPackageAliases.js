const fs = require("fs");

// "@open-smc/ui-kit" => "@open-smc/ui-kit/src"
module.exports = function (config) {
    if (!config.resolve) {
        config.resolve = {}
    }
    if (!config.resolve.alias) {
        config.resolve.alias = {}
    }
    fs.readdirSync(__dirname + "/packages")
        .forEach(packageName => {
            config.resolve.alias[`@open-smc/${packageName}`] = `@open-smc/${packageName}/src`;
        });
}
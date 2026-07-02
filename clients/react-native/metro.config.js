// Metro config for the monorepo: the app source-links @meshweaver/react/core (../react/src) rather than a
// published package, so Metro must watch that folder and resolve the alias. It also pins a SINGLE copy of
// react (../react has its own node_modules/react, which would give the source-linked core a second React
// instance → "Invalid hook call"), and stubs the live gRPC-web client for the offline demo.
const { getDefaultConfig } = require("expo/metro-config");
const path = require("path");
const fs = require("fs");

const projectRoot = __dirname;
const reactPkg = path.resolve(projectRoot, "../react");
const config = getDefaultConfig(projectRoot);

// Watch the source-linked renderer package.
config.watchFolders = [reactPkg];

const appReact = path.resolve(projectRoot, "node_modules/react");
const aliases = {
  "@meshweaver/react/core": path.resolve(reactPkg, "src/core.ts"),
  // Offline demo: don't bundle the browser/Node gRPC-web client (needs an RN fetch polyfill); stub it.
  "@meshweaver/client-web": path.resolve(projectRoot, "metro-stubs/client-web.ts"),
};

const base = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  if (aliases[moduleName]) return { type: "sourceFile", filePath: aliases[moduleName] };
  // Force the app's single react (defeats ../react/node_modules/react → dual-react hook errors).
  if (moduleName === "react") return { type: "sourceFile", filePath: path.join(appReact, "index.js") };
  if (moduleName === "react/jsx-runtime") return { type: "sourceFile", filePath: path.join(appReact, "jsx-runtime.js") };
  if (moduleName === "react/jsx-dev-runtime") return { type: "sourceFile", filePath: path.join(appReact, "jsx-dev-runtime.js") };
  // The source-linked @meshweaver/react is TS with ESM ".js" import specifiers that map to .tsx/.ts on
  // disk (NodeNext convention). Metro looks for the literal ".js"; remap to the real source file.
  if (moduleName.startsWith(".") && moduleName.endsWith(".js") && context.originModulePath.startsWith(reactPkg)) {
    const fromDir = path.dirname(context.originModulePath);
    for (const ext of [".tsx", ".ts"]) {
      const candidate = path.resolve(fromDir, moduleName.replace(/\.js$/, ext));
      if (fs.existsSync(candidate)) return { type: "sourceFile", filePath: candidate };
    }
  }
  return (base ?? context.resolveRequest)(context, moduleName, platform);
};

module.exports = config;

// Headless stand-in for react-native-svg (ships untranspiled Flow source vitest can't parse).
// SvgXml renders as a host node carrying its xml prop, enough to assert the Icon mapping.
import React from "react";

export const SvgXml = ({ xml, ...props }: any) => React.createElement("SvgXml", { xml, ...props });

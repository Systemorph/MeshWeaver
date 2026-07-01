import { useEffect, useState } from "react";
import { SafeAreaView, StatusBar } from "react-native";
import { RegistryProvider, ScopeProvider, StaticAreaSource, type AreaSource, type AreaTree } from "@meshweaver/react/core";
import { rnPack } from "./src/rnPack";
import { createLiveSource } from "./src/live";
import { NavContext, CurrentAddressContext, type NavTarget } from "./src/nav";
import { Shell, HOME } from "./src/Shell";
import { ensureWebStyles } from "./src/webStyles";
import { currentInstance } from "./src/connection";
import { type ClientDestination } from "./src/screens";
import { ThemeProvider, useTheme } from "./src/theme";

// The client connects to the CURRENT mesh instance — "Local" is the mesh that served this app
// (same origin, anonymous, no CORS); a remote instance is a portal the user added by URL + token
// (see screens.tsx → ConnectScreen). The shell drives navigation; each target re-subscribes the
// live source, and switching instance (instanceTick) reconnects and returns Home.
const emptyTree: AreaTree = { areas: {}, data: {} };

export default function App() {
  ensureWebStyles();
  return (
    <ThemeProvider>
      <AppInner />
    </ThemeProvider>
  );
}

function AppInner() {
  const { palette } = useTheme();
  const [nav, setNav] = useState<NavTarget>(HOME);
  const [clientScreen, setClientScreen] = useState<ClientDestination | null>(null);
  const [instanceTick, setInstanceTick] = useState(0);
  const [source, setSource] = useState<AreaSource>(() => new StaticAreaSource(emptyTree));

  const navigate = (t: NavTarget) => {
    setClientScreen(null);
    setNav(t);
  };
  const reconnect = () => {
    setClientScreen(null);
    setNav(HOME);
    setInstanceTick((t) => t + 1);
  };

  useEffect(() => {
    const inst = currentInstance();
    if (!inst.url) return;
    let live: Awaited<ReturnType<typeof createLiveSource>> | null = null;
    let cancelled = false;
    createLiveSource({ url: inst.url, token: inst.token, address: nav.address, area: nav.area })
      .then((l) => {
        if (cancelled) {
          l.connection.close();
          return;
        }
        live = l;
        setSource(l.source);
      })
      .catch(() => { /* connection failed — the shell stays on the last-good source */ });
    return () => {
      cancelled = true;
      live?.connection.close();
    };
  }, [nav.address, nav.area, instanceTick]);

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: palette.appBg }}>
      <StatusBar />
      <RegistryProvider pack={rnPack}>
        <NavContext.Provider value={navigate}>
          <CurrentAddressContext.Provider value={nav.address}>
            <ScopeProvider source={source} area={nav.area}>
              <Shell
                source={source}
                nav={nav}
                clientScreen={clientScreen}
                onNavigate={navigate}
                onClientScreen={setClientScreen}
                onReconnect={reconnect}
              />
            </ScopeProvider>
          </CurrentAddressContext.Provider>
        </NavContext.Provider>
      </RegistryProvider>
    </SafeAreaView>
  );
}

import { createContext, useContext, type ReactNode } from "react";
import { useUpdates } from "./useUpdates";

type UpdatesValue = ReturnType<typeof useUpdates>;

const UpdatesContext = createContext<UpdatesValue | null>(null);

/** One shared copy of the update state, so the banner, nav dot, System card and About all agree. */
export function UpdatesProvider({ children }: { children: ReactNode }) {
  const value = useUpdates();
  return <UpdatesContext.Provider value={value}>{children}</UpdatesContext.Provider>;
}

/** Null outside a provider, so callers must cope with the data being absent. */
// eslint-disable-next-line react-refresh/only-export-components
export function useUpdatesContext(): UpdatesValue | null {
  return useContext(UpdatesContext);
}

import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

/** The thing a view has selected on screen; the chat panel shows it and sends it along with a question. */
export interface Focus {
  kind: string;
  label: string;
  /** Symbol key, when the item is a symbol the server can look up. */
  key?: string;
}

type FocusState = [Focus | null, (f: Focus | null) => void];

const FocusContext = createContext<FocusState>([null, () => undefined]);

export function FocusProvider({ children }: { children: ReactNode }) {
  const state = useState<Focus | null>(null);
  return <FocusContext.Provider value={state}>{children}</FocusContext.Provider>;
}

export const useFocus = () => useContext(FocusContext)[0];

/** Publishes the view's selection while mounted; null clears it. */
export function usePublishFocus(focus: Focus | null) {
  const set = useContext(FocusContext)[1];
  const kind = focus?.kind;
  const label = focus?.label;
  const key = focus?.key;
  useEffect(() => {
    set(kind && label ? { kind, label, key } : null);
    return () => set(null);
  }, [set, kind, label, key]);
}

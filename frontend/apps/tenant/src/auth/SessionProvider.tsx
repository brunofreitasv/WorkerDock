import { createContext, useContext, type ReactNode } from "react";
import {
  QueryClient,
  QueryClientProvider,
  useQuery,
} from "@tanstack/react-query";
import { useState } from "react";
import { read } from "../api/client";
export interface Session {
  authenticated: boolean;
  canRead: boolean;
  canManage: boolean;
  mode: "Anonymous" | "Authenticated" | "RoleProtected";
  language: string;
  timeZone: string;
}
interface SessionState {
  session?: Session;
  loading: boolean;
  error: boolean;
  retry: () => void;
}
const Context = createContext<SessionState>({
  loading: true,
  error: false,
  retry: () => {},
});
function LoadSession({ children }: { children: ReactNode }) {
  const query = useQuery({
    queryKey: ["session"],
    queryFn: async () => (await read<Session>("/api/session")).data,
    retry: false,
    staleTime: 60_000,
  });
  return (
    <Context.Provider
      value={{
        session: query.data,
        loading: query.isPending,
        error: query.isError,
        retry: () => {
          void query.refetch();
        },
      }}
    >
      {children}
    </Context.Provider>
  );
}
export function SessionProvider({ children }: { children: ReactNode }) {
  const [client] = useState(() => new QueryClient());
  return (
    <QueryClientProvider client={client}>
      <LoadSession>{children}</LoadSession>
    </QueryClientProvider>
  );
}
export const useSession = () => useContext(Context);

import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 60_000,
      gcTime: 10 * 60_000,
      refetchOnMount: false,
      refetchOnWindowFocus: false,
    },
    mutations: { retry: false },
  },
});

export function QueryProvider({ children }: React.PropsWithChildren) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

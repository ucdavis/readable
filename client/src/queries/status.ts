import { useQuery } from '@tanstack/react-query';

export const useStatusBannerQuery = () => {
  return useQuery({
    queryFn: async (): Promise<string | null> => {
      const res = await fetch('/api/status/banner');
      if (res.status === 204 || !res.ok) return null;
      const data = (await res.json()) as { message: string };
      return data.message;
    },
    queryKey: ['status', 'banner'] as const,
    staleTime: 60_000, // 1 minute
    refetchInterval: 5 * 60_000, // re-check every 5 minutes
  });
};

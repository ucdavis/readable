import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '../lib/api.ts';

export type ApiKeyInfo = {
  exists: boolean;
  keyHint: string | null;
  createdAt: string | null;
};

export type GeneratedApiKey = {
  rawKey: string;
  keyHint: string;
  createdAt: string;
};

export const apiKeyQueryOptions = () => ({
  queryKey: ['apikey'] as const,
  queryFn: () => fetchJson<ApiKeyInfo>('/api/apikey'),
});

export const useApiKeyQuery = () => useQuery(apiKeyQueryOptions());

export const useGenerateApiKeyMutation = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () =>
      fetchJson<GeneratedApiKey>('/api/apikey', { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['apikey'] });
    },
  });
};

export const useRevokeApiKeyMutation = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => fetchJson<void>('/api/apikey', { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['apikey'] });
    },
  });
};

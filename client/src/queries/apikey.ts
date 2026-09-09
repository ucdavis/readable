import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '../lib/api.ts';

export type ApiKeyInfo = {
  createdAt: string | null;
  exists: boolean;
  keyHint: string | null;
};

export type GeneratedApiKey = {
  createdAt: string;
  keyHint: string;
  rawKey: string;
};

export const apiKeyQueryOptions = () => ({
  queryFn: () => fetchJson<ApiKeyInfo>('/api/apikey'),
  queryKey: ['apikey'] as const,
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

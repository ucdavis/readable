import { useQuery } from '@tanstack/react-query';
import { fetchJson } from '../lib/api.ts';

export type UserFile = {
  contentType: string;
  createdAt: string;
  fileId: string;
  originalFileName: string;
  sizeBytes: number;
  status: string;
  statusUpdatedAt: string;
};

export const myFilesQueryOptions = () => ({
  queryFn: async (): Promise<UserFile[]> => {
    return await fetchJson<UserFile[]>('/api/file');
  },
  queryKey: ['files', 'mine'] as const,
  staleTime: 30_000,
});

export const useMyFilesQuery = () => {
  return useQuery(myFilesQueryOptions());
};

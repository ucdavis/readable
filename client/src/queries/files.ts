import { useQuery } from '@tanstack/react-query';
import { fetchJson } from '../lib/api.ts';

export type AccessibilityReportListItem = {
  generatedAt: string;
  issueCount: number | null;
  reportId: number;
  stage: string;
  tool: string;
};

export type AccessibilityReportRuleItem = {
  Description?: string;
  Rule: string;
  Status: string;
};

export type AccessibilityReportJson = {
  'Detailed Report'?: Record<string, AccessibilityReportRuleItem[]>;
  Summary?: Record<string, number | string>;
};

export type AccessibilityReportDetails = AccessibilityReportListItem & {
  reportJson: AccessibilityReportJson;
};

export type UserFile = {
  accessibilityReports?: AccessibilityReportListItem[];
  contentType: string;
  createdAt: string;
  fileId: string;
  originalFileName: string;
  sizeBytes: number;
  status: string;
  statusUpdatedAt: string;
};

export type UserFileDetails = Omit<UserFile, 'accessibilityReports'> & {
  accessibilityReports: AccessibilityReportDetails[];
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

export const myFileQueryOptions = (fileId: string) => ({
  queryFn: async (): Promise<UserFileDetails> => {
    return await fetchJson<UserFileDetails>(
      `/api/file/${encodeURIComponent(fileId)}`
    );
  },
  queryKey: ['files', 'byId', fileId] as const,
  staleTime: 30_000,
});

export const useMyFileQuery = (fileId: string) => {
  return useQuery(myFileQueryOptions(fileId));
};

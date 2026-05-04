import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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

export type AccessibilityReportWarning = {
  code: string;
  message: string;
  stage: string;
};

export type UserFile = {
  accessibilityReports?: AccessibilityReportListItem[];
  contentType: string;
  createdAt: string;
  fileId: string;
  latestFailureReason?: string | null;
  originalFileName: string;
  sizeBytes: number;
  status: string;
  statusUpdatedAt: string;
};

export type UserFileDetails = Omit<UserFile, 'accessibilityReports'> & {
  accessibilityReports: AccessibilityReportDetails[];
  accessibilityReportWarnings?: AccessibilityReportWarning[];
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

export async function archiveFiles(fileIds: string[]): Promise<string[]> {
  return await fetchJson<string[]>('/api/file/archive', {
    body: JSON.stringify(fileIds),
    method: 'POST',
  });
}

export function useArchiveFilesMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (fileIds: string[]) => archiveFiles(fileIds),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['files'] });
    },
  });
}

export async function undeleteFiles(fileIds: string[]): Promise<string[]> {
  return await fetchJson<string[]>('/api/file/undelete', {
    body: JSON.stringify(fileIds),
    method: 'POST',
  });
}

export function useUndeleteFilesMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (fileIds: string[]) => undeleteFiles(fileIds),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['files'] });
    },
  });
}

/**
 * Initiates a streaming zip download containing the processed PDFs for the given file IDs.
 * The browser will prompt a file-save dialog via the Content-Disposition header set by the server.
 */
async function downloadFilesAsZip(fileIds: string[]): Promise<void> {
  const res = await fetch('/api/download/zip', {
    body: JSON.stringify(fileIds),
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
    },
    method: 'POST',
  });

  if (res.status === 401) {
    const toRedirectParam = encodeURIComponent(
      window.location.pathname + window.location.search
    );
    window.location.href = `/login?returnUrl=${toRedirectParam}`;
    return;
  }

  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Download failed (HTTP ${res.status})`);
  }

  // Stream the response blob into a download
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'readable-files.zip';
  document.body.append(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

export function useDownloadFilesAsZipMutation() {
  return useMutation({
    mutationFn: (fileIds: string[]) => downloadFilesAsZip(fileIds),
  });
}

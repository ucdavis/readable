import { fetchJson } from '../lib/api.ts';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  isLikelySasAuthError,
  isLikelyTransient,
  uploadToBlobWithSas,
  type UploadProgress,
} from '../upload/blobUpload.ts';

export type CreateUploadSasRequest = {
  contentType: string;
  originalFileName: string;
  sizeBytes: number;
};

export type CreateUploadSasResponse = {
  blobName: string;
  blobUrl: string;
  containerName: string;
  expiresAt: string;
  fileId: string;
  uploadUrl: string;
};

export async function createUploadSas(
  request: CreateUploadSasRequest,
  signal?: AbortSignal
): Promise<CreateUploadSasResponse> {
  return await fetchJson<CreateUploadSasResponse>(
    '/api/upload/sas',
    {
      body: JSON.stringify(request),
      method: 'POST',
    },
    signal
  );
}

export async function refreshUploadSas(
  fileId: string,
  signal?: AbortSignal
): Promise<CreateUploadSasResponse> {
  return await fetchJson<CreateUploadSasResponse>(
    `/api/upload/${encodeURIComponent(fileId)}/sas`,
    { method: 'POST' },
    signal
  );
}

export async function markUploadUploaded(
  fileId: string,
  signal?: AbortSignal
): Promise<void> {
  await fetchJson<void>(
    `/api/upload/${encodeURIComponent(fileId)}/uploaded`,
    { method: 'POST' },
    signal
  );
}

export type SasResponse = {
  blobUrl: string;
  sasUrl: string;
  uploadId: string;
};

export async function getUploadSas(
  file: File,
  signal?: AbortSignal
): Promise<SasResponse> {
  const response = await createUploadSas(
    {
      contentType: file.type || 'application/pdf',
      originalFileName: file.name,
      sizeBytes: file.size,
    },
    signal
  );

  return {
    blobUrl: response.blobUrl,
    sasUrl: response.uploadUrl,
    uploadId: response.fileId,
  };
}

export type BlobUploadResult = {
  blobUrl: string;
  uploadId: string;
};

export type UploadArgs = {
  file: File;
  onProgress?: (p: UploadProgress) => void;
  onStarted?: (args: BlobUploadResult) => void;
  signal?: AbortSignal;
};

export function useBlobUploadMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({
      file,
      onProgress,
      onStarted,
      signal,
    }: UploadArgs): Promise<BlobUploadResult> => {
      const sas = await getUploadSas(file, signal);
      onStarted?.({ blobUrl: sas.blobUrl, uploadId: sas.uploadId });

      try {
        await uploadToBlobWithSas({
          file,
          onProgress,
          sasUrl: sas.sasUrl,
          signal,
        });
      } catch (error) {
        // One-time “refresh SAS” on 403; backend keeps the same uploadId.
        if (isLikelySasAuthError(error)) {
          const refreshed = await refreshUploadSas(sas.uploadId, signal);
          await uploadToBlobWithSas({
            file,
            onProgress,
            sasUrl: refreshed.uploadUrl,
            signal,
          });
        } else {
          throw error;
        }
      }

      // after upload completion, mark the file as uploaded
      try {
        await markUploadUploaded(sas.uploadId, signal);
      } catch {
        // ignore
      }

      return { blobUrl: sas.blobUrl, uploadId: sas.uploadId };
    },

    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['files', 'mine'] });
    },

    retry: (failureCount, err) => {
      if (failureCount >= 2) {
        return false;
      }
      // Don’t let React Query retry auth problems; 403 is handled inside mutationFn.
      if (isLikelySasAuthError(err)) {
        return false;
      }
      return isLikelyTransient(err);
    },

    retryDelay: (attempt) => Math.min(1000 * 2 ** attempt, 8000),
  });
}

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { getUploadSas, refreshUploadSas } from './upload.ts';
import {
  isLikelySasAuthError,
  isLikelyTransient,
  uploadToBlobWithSas,
  type UploadProgress,
} from '../upload/blobUpload.ts';

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

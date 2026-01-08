import { useCallback, useMemo, useRef, useState } from 'react';
import { useBlobUploadMutation } from '@/queries/upload.ts';
import { isAbortError, type UploadProgress } from '@/upload/blobUpload.ts';

export type UploadRow = {
  error?: string;
  fileName: string;
  percent: number;
  sizeBytes: number;
  state: 'uploading';
  uploadId: string;
};

type UsePdfUploadsArgs = {
  onUploadStarted?: () => void;
};

export function usePdfUploads({ onUploadStarted }: UsePdfUploadsArgs = {}) {
  const blobUpload = useBlobUploadMutation();

  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadsByFileId, setUploadsByFileId] = useState<
    Record<string, UploadRow>
  >({});
  const activeUploadCount = useMemo(
    () =>
      Object.values(uploadsByFileId).filter((u) => u.state === 'uploading')
        .length,
    [uploadsByFileId]
  );

  // Abort controllers must be reachable by callbacks without triggering rerenders.
  const abortRef = useRef<Record<string, AbortController>>({});

  const startUpload = useCallback(
    async (file: File) => {
      if (!looksLikePdf(file)) {
        setUploadError('Only PDF uploads are supported.');
        return;
      }

      setUploadError(null);
      const abortController = new AbortController();
      // We only learn the server-generated uploadId after `onStarted` fires.
      let startedUploadId: string | null = null;
      // Avoid redundant state updates when percent hasn't changed.
      let lastPercent = -1;

      try {
        const result = await blobUpload.mutateAsync({
          file,
          onProgress: (p: UploadProgress) => {
            const uploadId = startedUploadId;
            if (!uploadId) {
              return;
            }
            if (p.percent === lastPercent) {
              return;
            }
            lastPercent = p.percent;
            setUploadsByFileId((prev) => {
              const existing = prev[uploadId];
              if (!existing) {
                return prev;
              }
              return {
                ...prev,
                [uploadId]: { ...existing, percent: p.percent },
              };
            });
          },
          onStarted: ({ uploadId }) => {
            startedUploadId = uploadId;
            abortRef.current[uploadId] = abortController;
            // We only track active uploads; successful uploads are removed from this map.
            setUploadsByFileId((prev) => ({
              ...prev,
              [uploadId]: {
                fileName: file.name,
                percent: 0,
                sizeBytes: file.size,
                state: 'uploading',
                uploadId,
              },
            }));
            onUploadStarted?.();
          },
          signal: abortController.signal,
        });

        setUploadsByFileId((prev) => {
          const next = { ...prev };
          delete next[result.uploadId];
          return next;
        });
      } catch (error) {
        setUploadError(error instanceof Error ? error.message : String(error));
        const uploadId = startedUploadId;
        if (uploadId && isAbortError(error)) {
          setUploadsByFileId((prev) => {
            const next = { ...prev };
            delete next[uploadId];
            return next;
          });
        }
      } finally {
        const uploadId = startedUploadId;
        if (uploadId) {
          delete abortRef.current[uploadId];
        }
      }
    },
    [blobUpload, onUploadStarted]
  );

  const cancelUpload = useCallback((uploadId: string) => {
    const controller = abortRef.current[uploadId];
    if (!controller) {
      return;
    }
    controller.abort();
    setUploadsByFileId((prev) => {
      const next = { ...prev };
      delete next[uploadId];
      return next;
    });
  }, []);

  return {
    abortRef,
    activeUploadCount,
    blobUpload,
    cancelUpload,
    startUpload,
    uploadError,
    uploadsByFileId,
  };
}

function looksLikePdf(file: File) {
  if (file.type === 'application/pdf') {
    return true;
  }
  return file.name.toLowerCase().endsWith('.pdf');
}

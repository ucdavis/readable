import { createFileRoute } from '@tanstack/react-router';
import { useCallback, useEffect } from 'react';
import { PdfActivityCard } from '@/components/pdf/PdfActivityCard.tsx';
import { PdfUploadDropzone } from '@/components/pdf/PdfUploadDropzone.tsx';
import { myFilesQueryOptions } from '@/queries/files.ts';
import { useQuery } from '@tanstack/react-query';
import { useFileActivityPolling } from '@/lib/useFileActivityPolling.ts';
import { usePdfUploads } from '@/lib/usePdfUploads.ts';
import { useScheduledRefetch } from '@/lib/useScheduledRefetch.ts';

export const Route = createFileRoute('/(authenticated)/pdf')({
  component: RouteComponent,
});

function RouteComponent() {
  const { observeFiles, pollMs, recentlyCompletedByFileId } =
    useFileActivityPolling();
  const filesQuery = useQuery({
    ...myFilesQueryOptions(),
    refetchInterval: pollMs,
    refetchIntervalInBackground: false,
  });

  // Keeps the Activity list feeling “live” while server-side processing is happening.
  useEffect(() => {
    observeFiles(filesQuery.data);
  }, [filesQuery.data, observeFiles]);

  const scheduleFilesRefresh = useScheduledRefetch(filesQuery.refetch, 250);

  const {
    abortRef,
    activeUploadCount,
    blobUpload,
    cancelUpload,
    startUpload,
    uploadError,
    uploadsByFileId,
  } = usePdfUploads({ onUploadStarted: scheduleFilesRefresh });

  const handleFilesSelected = useCallback(
    (files: File[]) => {
      for (const file of files) {
        void startUpload(file);
      }
    },
    [startUpload]
  );

  const canCancelUpload = useCallback(
    (fileId: string) => abortRef.current[fileId] !== undefined,
    [abortRef]
  );

  return (
    <div className="container">
      <header className="space-y-1 my-5">
        <h1 className="text-3xl sm:text-4xl font-extrabold tracking-tight">
          Readable
        </h1>
        <p className="text-base-content/70">
          PDF Accessibility Conversion Tool
        </p>
      </header>

      {/* Upload Dropzone */}
      <PdfUploadDropzone
        isUploading={blobUpload.isPending}
        onFilesSelected={handleFilesSelected}
      />

      {uploadError ? (
        <div className="alert alert-error">
          <span>Upload failed: {uploadError}</span>
        </div>
      ) : null}

      <PdfActivityCard
        activeUploadCount={activeUploadCount}
        canCancelUpload={canCancelUpload}
        files={filesQuery.data}
        isError={filesQuery.isError}
        onCancelUpload={cancelUpload}
        recentlyCompletedByFileId={recentlyCompletedByFileId}
        uploadsByFileId={uploadsByFileId}
      />
    </div>
  );
}

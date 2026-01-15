import { PdfActivityCard } from '@/components/pdf/PdfActivityCard.tsx';
import { PdfUploadDropzone } from '@/components/pdf/PdfUploadDropzone.tsx';
import { useFileActivityPolling } from '@/lib/useFileActivityPolling.ts';
import { usePdfUploads } from '@/lib/usePdfUploads.ts';
import { useScheduledRefetch } from '@/lib/useScheduledRefetch.ts';
import { myFilesQueryOptions } from '@/queries/files.ts';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useCallback, useEffect } from 'react';
import { Link } from '@tanstack/react-router';

export const Route = createFileRoute('/(authenticated)/')({
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
      <div className="text-center mb-4">
        <h3 className="text-lg font-extrabold">Make Your PDFs Accessible</h3>
        <p className="max-w-prose mx-auto">
          Readable helps you meet modern accessibility requirements by
          transforming standard PDFs into documents that are more compliant with{' '}
          <a
            className="link"
            href="https://digitalaccessibility.ucop.edu/index.html"
            rel="noopener noreferrer"
            target="_blank"
          >
            WCAG and PDF/UA guidelines
          </a>
          .
        </p>
        <Link className="btn btn-outline btn-sm btn-primary mt-3" to="/FAQ">
          More Info
        </Link>
      </div>
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

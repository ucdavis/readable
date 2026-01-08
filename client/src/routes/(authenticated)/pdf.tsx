import { createFileRoute } from '@tanstack/react-router';
import {
  type ChangeEventHandler,
  type DragEventHandler,
  useRef,
  useState,
} from 'react';
import { useMyFilesQuery } from '@/queries/files.ts';
import { useBlobUploadMutation } from '@/queries/upload.ts';
import { isAbortError, type UploadProgress } from '@/upload/blobUpload.ts';

export const Route = createFileRoute('/(authenticated)/pdf')({
  component: RouteComponent,
});

type UploadRow = {
  error?: string;
  fileName: string;
  percent: number;
  sizeBytes: number;
  state: 'uploading' | 'success' | 'error' | 'cancelled';
  uploadId: string;
};

function RouteComponent() {
  const [isDragging, setIsDragging] = useState(false);
  const filesQuery = useMyFilesQuery();
  const blobUpload = useBlobUploadMutation();

  const [uploads, setUploads] = useState<UploadRow[]>([]);
  const abortRef = useRef<Record<string, AbortController>>({});
  const fileListRefreshTimeoutRef = useRef<number | null>(null);

  const upsertUpload = (
    partial: Partial<UploadRow> & Pick<UploadRow, 'uploadId'>
  ) => {
    setUploads((prev) => {
      const index = prev.findIndex((u) => u.uploadId === partial.uploadId);
      if (index < 0) {
        return prev;
      }
      const next = [...prev];
      next[index] = { ...next[index], ...partial };
      return next;
    });
  };

  const scheduleFilesRefresh = () => {
    if (fileListRefreshTimeoutRef.current !== null) {
      return;
    }
    fileListRefreshTimeoutRef.current = window.setTimeout(() => {
      fileListRefreshTimeoutRef.current = null;
      void filesQuery.refetch();
    }, 250);
  };

  const startUpload = async (file: File) => {
    if (!looksLikePdf(file)) {
      setUploads((prev) => [
        {
          error: 'Only PDF uploads are supported.',
          fileName: file.name,
          percent: 0,
          sizeBytes: file.size,
          state: 'error',
          uploadId: `${file.name}-${file.size}-${file.lastModified}`,
        },
        ...prev,
      ]);
      return;
    }

    const abortController = new AbortController();
    let startedUploadId: string | null = null;
    let lastPercent = -1;

    try {
      const result = await blobUpload.mutateAsync({
        file,
        onProgress: (p: UploadProgress) => {
          if (startedUploadId === null) {
            return;
          }
          if (p.percent === lastPercent) {
            return;
          }
          lastPercent = p.percent;
          upsertUpload({ percent: p.percent, uploadId: startedUploadId });
        },
        onStarted: ({ uploadId }) => {
          startedUploadId = uploadId;
          abortRef.current[uploadId] = abortController;
          setUploads((prev) => [
            {
              fileName: file.name,
              percent: 0,
              sizeBytes: file.size,
              state: 'uploading',
              uploadId,
            },
            ...prev,
          ]);
          scheduleFilesRefresh();
        },
        signal: abortController.signal,
      });

      upsertUpload({
        percent: 100,
        state: 'success',
        uploadId: result.uploadId,
      });
    } catch (error) {
      if (startedUploadId !== null) {
        upsertUpload({
          error: error instanceof Error ? error.message : String(error),
          state: isAbortError(error) ? 'cancelled' : 'error',
          uploadId: startedUploadId,
        });
      } else {
        setUploads((prev) => [
          {
            error: error instanceof Error ? error.message : String(error),
            fileName: file.name,
            percent: 0,
            sizeBytes: file.size,
            state: isAbortError(error) ? 'cancelled' : 'error',
            uploadId: `${file.name}-${file.size}-${file.lastModified}`,
          },
          ...prev,
        ]);
      }
    } finally {
      if (startedUploadId !== null) {
        delete abortRef.current[startedUploadId];
      }
    }
  };

  const handleDragOver: DragEventHandler<HTMLDivElement> = (e) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(true);
  };

  const handleDragLeave: DragEventHandler<HTMLDivElement> = (e) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
  };

  const handleDrop: DragEventHandler<HTMLDivElement> = (e) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
    const files = Array.from(e.dataTransfer.files ?? []);
    for (const file of files) {
      void startUpload(file);
    }
  };

  const handleFileInput: ChangeEventHandler<HTMLInputElement> = (e) => {
    const files = Array.from(e.target.files ?? []);
    for (const file of files) {
      void startUpload(file);
    }
    e.target.value = '';
  };

  const cancelUpload = (uploadId: string) => {
    const controller = abortRef.current[uploadId];
    if (!controller) {
      return;
    }
    controller.abort();
    upsertUpload({ state: 'cancelled', uploadId });
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-base-100 to-base-200">
      <div className="container mx-auto max-w-6xl px-4 py-8 space-y-6">
        <header className="space-y-1">
          <h1 className="text-3xl sm:text-4xl font-extrabold tracking-tight">
            PDF Accessibility Tool
          </h1>
          <p className="text-base-content/70">
            Drop PDFs to queue remediation (upload wiring coming next).
          </p>
        </header>

        {/* Upload Dropzone */}
        <div
          className={`mb-6 border-2 border-dashed rounded-lg p-8 text-center transition-colors min-h-[33vh] flex flex-col items-center justify-center ${
            isDragging
              ? 'border-primary bg-primary/10'
              : 'border-base-300 bg-base-100 hover:border-base-content/30'
          }`}
          onDragLeave={handleDragLeave}
          onDragOver={handleDragOver}
          onDrop={handleDrop}
        >
          <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-base-200 text-base-content/70">
            <span className="text-xs font-semibold">PDF</span>
          </div>
          <h3 className="text-base-content mb-2 text-lg font-semibold">
            Upload PDF Files
          </h3>
          <p className="text-base-content/70 mb-4">
            Drag and drop your PDF files here, or select files to browse.
          </p>
          <input
            accept=".pdf,application/pdf"
            className="hidden"
            id="file-upload"
            multiple
            onChange={handleFileInput}
            type="file"
          />
          <label
            className="btn btn-primary inline-flex items-center gap-2"
            htmlFor="file-upload"
          >
            Select Files
          </label>
          <div className="mt-3 text-xs text-base-content/60">
            PDF only · Multiple files supported
          </div>
          {blobUpload.isPending ? (
            <div className="mt-4 flex items-center gap-3 text-sm text-base-content/70">
              <span className="loading loading-spinner loading-sm" />
              <span>Uploading…</span>
            </div>
          ) : null}
        </div>

        {uploads.length > 0 ? (
          <div className="card bg-base-100 shadow">
            <div className="card-body">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="card-title">Uploads</h2>
                <button
                  className="btn btn-sm btn-outline"
                  onClick={() => setUploads([])}
                  type="button"
                >
                  Clear
                </button>
              </div>

              <div className="space-y-3">
                {uploads.slice(0, 5).map((upload) => (
                  <div
                    className="rounded-box border border-base-300 p-3"
                    key={upload.uploadId}
                  >
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                      <div className="min-w-0">
                        <div className="font-medium truncate">
                          {upload.fileName}{' '}
                          <span className="text-base-content/60 font-normal">
                            ({formatBytes(upload.sizeBytes)})
                          </span>
                        </div>
                        <div className="text-xs text-base-content/60">
                          UploadId: {upload.uploadId}
                        </div>
                      </div>

                      <div className="flex items-center gap-2">
                        {upload.state === 'uploading' ? (
                          <span className="badge badge-info">Uploading</span>
                        ) : upload.state === 'success' ? (
                          <span className="badge badge-success">Complete</span>
                        ) : upload.state === 'cancelled' ? (
                          <span className="badge badge-ghost">Cancelled</span>
                        ) : (
                          <span className="badge badge-error">Failed</span>
                        )}
                        <button
                          className="btn btn-sm btn-outline"
                          disabled={
                            abortRef.current[upload.uploadId] === undefined
                          }
                          onClick={() => cancelUpload(upload.uploadId)}
                          type="button"
                        >
                          Cancel
                        </button>
                      </div>
                    </div>

                    <div className="mt-3 space-y-2">
                      <div className="flex items-center justify-between text-sm">
                        <span className="text-base-content/70">
                          {upload.state === 'uploading'
                            ? 'Uploading…'
                            : 'Progress'}
                        </span>
                        {upload.state === 'uploading' ? (
                          <span className="text-base-content/60">In progress</span>
                        ) : (
                          <span className="text-base-content/70">
                            {upload.percent}%
                          </span>
                        )}
                      </div>
                      {upload.state === 'uploading' ? (
                        <progress className="progress progress-primary w-full" />
                      ) : (
                        <progress
                          className="progress progress-primary w-full"
                          max={100}
                          value={upload.state === 'success' ? 100 : upload.percent}
                        />
                      )}
                      {upload.error ? (
                        <div className="text-sm text-error">{upload.error}</div>
                      ) : null}
                    </div>
                  </div>
                ))}
                {uploads.length > 5 ? (
                  <div className="text-sm text-base-content/60">
                    Showing 5 of {uploads.length} uploads.
                  </div>
                ) : null}
              </div>
            </div>
          </div>
        ) : null}

        <div className="card bg-base-100 shadow">
          <div className="card-body">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="card-title">Activity</h2>
              <button
                className="btn btn-sm btn-outline"
                disabled={filesQuery.isFetching}
                onClick={() => filesQuery.refetch()}
                type="button"
              >
                {filesQuery.isFetching ? 'Refreshing…' : 'Refresh'}
              </button>
            </div>

            <div className="overflow-x-auto">
              <table className="table table-zebra">
                <thead>
                  <tr>
                    <th>File</th>
                    <th>Status</th>
                    <th className="text-right">Size</th>
                    <th>Created</th>
                  </tr>
                </thead>
                <tbody>
                  {filesQuery.isError ? (
                    <tr>
                      <td colSpan={4}>
                        <div className="alert alert-error">
                          <span>Failed to load your files.</span>
                        </div>
                      </td>
                    </tr>
                  ) : filesQuery.data === undefined ? (
                    <tr>
                      <td className="text-base-content/70" colSpan={4}>
                        <div className="flex items-center gap-3">
                          <span className="loading loading-spinner loading-sm" />
                          <span>Loading…</span>
                        </div>
                      </td>
                    </tr>
                  ) : filesQuery.data.length === 0 ? (
                    <tr>
                      <td className="text-base-content/60" colSpan={4}>
                        No files yet.
                      </td>
                    </tr>
                  ) : (
                    filesQuery.data.map((file) => (
                      <tr key={file.fileId}>
                        <td className="font-medium">{file.originalFileName}</td>
                        <td>
                          <span className="badge badge-ghost">
                            {file.status}
                          </span>
                        </td>
                        <td className="text-right">
                          {formatBytes(file.sizeBytes)}
                        </td>
                        <td>{formatDate(file.createdAt)}</td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function looksLikePdf(file: File) {
  if (file.type === 'application/pdf') {
    return true;
  }
  return file.name.toLowerCase().endsWith('.pdf');
}

function formatDate(iso: string) {
  const d = new Date(iso);
  return Number.isNaN(d.valueOf()) ? iso : d.toLocaleString();
}

function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes)) {
    return '';
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB', 'TB'] as const;
  let value = bytes / 1024;
  let unit: (typeof units)[number] = units[0];
  for (let i = 1; i < units.length && value >= 1024; i++) {
    value /= 1024;
    unit = units[i];
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${unit}`;
}

import { createFileRoute } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { useMyFilesQuery } from '../../queries/files.ts';
import { useBlobUploadMutation } from '../../queries/blobUploadMutation.ts';
import { isAbortError, type UploadProgress } from '../../upload/blobUpload.ts';

export const Route = createFileRoute('/(authenticated)/upload')({
  component: RouteComponent,
});

type UploadRow = {
  blobUrl: string;
  error?: string;
  fileName: string;
  percent: number;
  sizeBytes: number;
  state: 'uploading' | 'success' | 'error' | 'cancelled';
  uploadId: string;
};

function RouteComponent() {
  const filesQuery = useMyFilesQuery();
  const blobUpload = useBlobUploadMutation();

  const [creatingUpload, setCreatingUpload] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploads, setUploads] = useState<UploadRow[]>([]);
  const abortRef = useRef<Record<string, AbortController>>({});

  const canPickFile = !creatingUpload && !blobUpload.isPending;

  const upsertUpload = (partial: Partial<UploadRow> & Pick<UploadRow, 'uploadId'>) => {
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

  const startUpload = async (file: File) => {
    if (!looksLikePdf(file)) {
      setUploads((prev) => [
        {
          blobUrl: '',
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

    setUploadError(null);
    const abortController = new AbortController();
    let startedUploadId: string | null = null;
    let lastPercent = -1;

    try {
      setCreatingUpload(true);

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
        onStarted: ({ blobUrl, uploadId }) => {
          startedUploadId = uploadId;
          abortRef.current[uploadId] = abortController;
          setCreatingUpload(false);
          setUploads((prev) => [
            {
              blobUrl,
              fileName: file.name,
              percent: 0,
              sizeBytes: file.size,
              state: 'uploading',
              uploadId,
            },
            ...prev,
          ]);
        },
        signal: abortController.signal,
      });

      upsertUpload({
        blobUrl: result.blobUrl,
        percent: 100,
        state: 'success',
        uploadId: result.uploadId,
      });
    } catch (error) {
      setCreatingUpload(false);
      if (startedUploadId !== null) {
        upsertUpload({
          error: error instanceof Error ? error.message : String(error),
          state: isAbortError(error) ? 'cancelled' : 'error',
          uploadId: startedUploadId,
        });
      } else {
        setUploadError(error instanceof Error ? error.message : String(error));
      }
    } finally {
      setCreatingUpload(false);
      if (startedUploadId !== null) {
        delete abortRef.current[startedUploadId];
      }
    }
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
    <div className="container mx-auto max-w-5xl p-4 space-y-8">
      <div className="card bg-base-100 shadow">
        <div className="card-body">
          <h1 className="card-title">Upload</h1>
          <input
            accept=".pdf,application/pdf"
            className="file-input file-input-bordered w-full"
            disabled={!canPickFile}
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (!file) {
                return;
              }
              void startUpload(file);
              e.target.value = '';
            }}
            type="file"
          />

          <p className="text-sm text-base-content/70">
            Select a PDF to create a record and upload it to blob storage.
          </p>

          {creatingUpload ? (
            <div className="flex items-center gap-3 text-base-content/70">
              <span className="loading loading-spinner loading-sm" />
              <span>Creating upload URL…</span>
            </div>
          ) : null}

          {uploadError ? (
            <div className="alert alert-error">
              <span>Upload failed: {uploadError}</span>
            </div>
          ) : null}

          {uploads.length > 0 ? (
            <div className="space-y-3">
              {uploads.map((upload) => (
                <div className="rounded-box border border-base-300 p-3" key={upload.uploadId}>
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
                        disabled={abortRef.current[upload.uploadId] === undefined}
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
                        {upload.state === 'uploading' ? 'Uploading…' : 'Progress'}
                      </span>
                      <span className="text-base-content/70">{upload.percent}%</span>
                    </div>
                    <progress
                      className="progress progress-primary w-full"
                      max={100}
                      value={upload.percent}
                    />
                    {upload.error ? (
                      <div className="text-sm text-error">{upload.error}</div>
                    ) : null}
                  </div>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      </div>

      <div className="card bg-base-100 shadow">
        <div className="card-body">
          <div className="flex items-center justify-between gap-4">
            <h2 className="card-title">Your files</h2>
            <button
              className="btn btn-sm btn-outline"
              disabled={filesQuery.isFetching}
              onClick={() => filesQuery.refetch()}
              type="button"
            >
              {filesQuery.isFetching ? 'Refreshing…' : 'Refresh'}
            </button>
          </div>

          {filesQuery.isError ? (
            <div className="alert alert-error">
              <span>Failed to load your files.</span>
            </div>
          ) : filesQuery.data === undefined ? (
            <div className="flex items-center gap-3 text-base-content/70">
              <span className="loading loading-spinner loading-sm" />
              <span>Loading…</span>
            </div>
          ) : filesQuery.data.length === 0 ? (
            <div className="text-base-content/70">No files yet.</div>
          ) : (
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
                  {filesQuery.data.map((file) => (
                    <tr key={file.fileId}>
                      <td className="font-medium">{file.originalFileName}</td>
                      <td>{file.status}</td>
                      <td className="text-right">
                        {formatBytes(file.sizeBytes)}
                      </td>
                      <td>{formatDate(file.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
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

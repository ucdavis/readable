import { createFileRoute } from '@tanstack/react-router';
import {
  type ChangeEventHandler,
  type DragEventHandler,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { myFilesQueryOptions } from '@/queries/files.ts';
import { useBlobUploadMutation } from '@/queries/upload.ts';
import { isAbortError, type UploadProgress } from '@/upload/blobUpload.ts';
import { useQuery } from '@tanstack/react-query';

export const Route = createFileRoute('/(authenticated)/pdf')({
  component: RouteComponent,
});

const IN_PROGRESS_STATUSES = new Set(['Created', 'Queued', 'Processing']);

type UploadRow = {
  error?: string;
  fileName: string;
  percent: number;
  sizeBytes: number;
  state: 'uploading';
  uploadId: string;
};

function RouteComponent() {
  const [isDragging, setIsDragging] = useState(false);
  const [pollMs, setPollMs] = useState<number | false>(false);
  const filesQuery = useQuery({
    ...myFilesQueryOptions(),
    refetchInterval: pollMs,
    refetchIntervalInBackground: false,
  });
  const blobUpload = useBlobUploadMutation();

  const [uploadError, setUploadError] = useState<string | null>(null);
  const [uploadsByFileId, setUploadsByFileId] = useState<
    Record<string, UploadRow>
  >({});
  const [recentlyCompletedByFileId, setRecentlyCompletedByFileId] = useState<
    Record<string, number>
  >({});
  const activeUploadCount = useMemo(
    () =>
      Object.values(uploadsByFileId).filter((u) => u.state === 'uploading')
        .length,
    [uploadsByFileId]
  );

  const abortRef = useRef<Record<string, AbortController>>({});
  const fileListRefreshTimeoutRef = useRef<number | null>(null);
  const recentlyCompletedTimersRef = useRef<Record<string, number>>({});
  const hasSeenInitialFilesRef = useRef(false);
  const prevStatusByFileIdRef = useRef<Record<string, string>>({});

  // Keeps the Activity list feeling “live” while server-side processing is happening:
  // - enables polling when any file is in progress
  // - uses a small backoff when nothing changes
  // - highlights files that *just* transitioned to Completed (this session)
  useEffect(() => {
    const files = filesQuery.data;
    if (!files) {
      return;
    }

    const hasInProgressFiles = files.some((f) =>
      IN_PROGRESS_STATUSES.has(f.status)
    );

    const currentStatusById: Record<string, string> = {};
    for (const f of files) {
      currentStatusById[f.fileId] = f.status;
    }

    if (!hasSeenInitialFilesRef.current) {
      hasSeenInitialFilesRef.current = true;
      prevStatusByFileIdRef.current = currentStatusById;
      setPollMs(hasInProgressFiles ? 2000 : false);
      return;
    }

    let anyStatusChanged = false;
    const prev = prevStatusByFileIdRef.current;

    for (const f of files) {
      const prevStatus = prev[f.fileId];
      if (prevStatus && prevStatus !== f.status) {
        anyStatusChanged = true;

        if (prevStatus !== 'Completed' && f.status === 'Completed') {
          const fileId = f.fileId;

          setRecentlyCompletedByFileId((prevRecent) => ({
            ...prevRecent,
            [fileId]: Date.now(),
          }));

          const existingTimer = recentlyCompletedTimersRef.current[fileId];
          if (existingTimer) {
            window.clearTimeout(existingTimer);
          }

          recentlyCompletedTimersRef.current[fileId] = window.setTimeout(() => {
            setRecentlyCompletedByFileId((prevRecent) => {
              if (!(fileId in prevRecent)) {
                return prevRecent;
              }
              const next = { ...prevRecent };
              delete next[fileId];
              return next;
            });
            delete recentlyCompletedTimersRef.current[fileId];
          }, 30_000);
        }
      }
    }

    prevStatusByFileIdRef.current = currentStatusById;

    if (!hasInProgressFiles) {
      setPollMs(false);
      return;
    }

    if (anyStatusChanged) {
      setPollMs(2000);
      return;
    }

    setPollMs((prevMs) => {
      if (prevMs === false) {
        return 2000;
      }
      return Math.min(prevMs + 1000, 8000);
    });
  }, [filesQuery.data]);

  // Cleanup timers on unmount (scheduled refetch + "recently completed" timers).
  useEffect(() => {
    return () => {
      if (fileListRefreshTimeoutRef.current !== null) {
        window.clearTimeout(fileListRefreshTimeoutRef.current);
        fileListRefreshTimeoutRef.current = null;
      }
      for (const timer of Object.values(recentlyCompletedTimersRef.current)) {
        window.clearTimeout(timer);
      }
      recentlyCompletedTimersRef.current = {};
    };
  }, []);

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
      setUploadError('Only PDF uploads are supported.');
      return;
    }

    setUploadError(null);
    const abortController = new AbortController();
    let startedUploadId: string | null = null;
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
          scheduleFilesRefresh();
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
    setUploadsByFileId((prev) => {
      const next = { ...prev };
      delete next[uploadId];
      return next;
    });
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

        {uploadError ? (
          <div className="alert alert-error">
            <span>Upload failed: {uploadError}</span>
          </div>
        ) : null}

        <div className="card bg-base-100 shadow">
          <div className="card-body">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="card-title">Activity</h2>
              <div className="flex items-center gap-2">
                {activeUploadCount > 0 ? (
                  <span className="badge badge-info badge-outline">
                    Uploading {activeUploadCount}
                  </span>
                ) : null}
              </div>
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
                          <div className="space-y-2">
                            <div className="flex flex-wrap items-center gap-2">
                              <span className="badge badge-ghost">
                                {file.status}
                              </span>
                              {recentlyCompletedByFileId[file.fileId] ? (
                                <span className="badge badge-success badge-outline">
                                  Just completed
                                </span>
                              ) : null}
                              {uploadsByFileId[file.fileId] ? (
                                <button
                                  className="btn btn-xs btn-outline"
                                  disabled={
                                    abortRef.current[file.fileId] === undefined
                                  }
                                  onClick={() => cancelUpload(file.fileId)}
                                  type="button"
                                >
                                  Cancel upload
                                </button>
                              ) : null}
                            </div>
                            {uploadsByFileId[file.fileId] ? (
                              <progress className="progress progress-primary w-full" />
                            ) : null}
                          </div>
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

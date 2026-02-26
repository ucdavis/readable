import type { UserFile } from '@/queries/files.ts';
import type { UploadRow } from '@/lib/usePdfUploads.ts';
import {
  useArchiveFilesMutation,
  useUndeleteFilesMutation,
} from '@/queries/files.ts';
import { formatBytes, formatDateTime } from '@/lib/format.ts';
import { Link } from '@tanstack/react-router';
import { ArrowDownTrayIcon } from '@heroicons/react/24/solid';
import {
  ArrowUturnLeftIcon,
  TrashIcon,
  DocumentChartBarIcon,
} from '@heroicons/react/24/outline';
import { useCallback, useRef, useState } from 'react';

export type PdfActivityCardProps = {
  activeUploadCount: number;
  canCancelUpload: (fileId: string) => boolean;
  files: UserFile[] | undefined;
  isError: boolean;
  onCancelUpload: (fileId: string) => void;
  recentlyCompletedByFileId: Record<string, number>;
  uploadsByFileId: Record<string, UploadRow>;
};

export function PdfActivityCard({
  activeUploadCount,
  canCancelUpload,
  files,
  isError,
  onCancelUpload,
  recentlyCompletedByFileId,
  uploadsByFileId,
}: PdfActivityCardProps) {
  const [filter, setFilter] = useState('');
  const [hiddenIds, setHiddenIds] = useState<Set<string>>(new Set());
  const [archiveError, setArchiveError] = useState<string | null>(null);
  const confirmDialogRef = useRef<HTMLDialogElement>(null);
  const [pendingBulkIds, setPendingBulkIds] = useState<string[]>([]);
  const [recentlyDeletedIds, setRecentlyDeletedIds] = useState<string[]>([]);

  const archiveMutation = useArchiveFilesMutation();
  const undeleteMutation = useUndeleteFilesMutation();

  const visibleFiles = files?.filter((f) => !hiddenIds.has(f.fileId));
  const filteredFiles = visibleFiles?.filter((f) =>
    f.originalFileName.toLowerCase().includes(filter.toLowerCase())
  );

  const isFiltered = filter.length > 0;
  const bulkTargetFiles = isFiltered ? filteredFiles : visibleFiles;
  const bulkCount = bulkTargetFiles?.length ?? 0;

  const handleArchiveSuccess = useCallback((archivedIds: string[]) => {
    setHiddenIds((prev) => {
      const next = new Set(prev);
      for (const id of archivedIds) {
        next.add(id);
      }
      return next;
    });
    setRecentlyDeletedIds((prev) => [
      ...prev,
      ...archivedIds.filter((id) => !prev.includes(id)),
    ]);
    setArchiveError(null);
  }, []);

  const handleArchiveError = useCallback((error: unknown) => {
    const message =
      error instanceof Error ? error.message : 'Failed to archive files.';
    setArchiveError(message);
  }, []);

  const archiveSingle = useCallback(
    (fileId: string) => {
      archiveMutation.mutate([fileId], {
        onError: handleArchiveError,
        onSuccess: handleArchiveSuccess,
      });
    },
    [archiveMutation, handleArchiveError, handleArchiveSuccess]
  );

  const openBulkConfirmation = useCallback(() => {
    const ids = (bulkTargetFiles ?? []).map((f) => f.fileId);
    if (ids.length === 0) {
      return;
    }
    setPendingBulkIds(ids);
    confirmDialogRef.current?.showModal();
  }, [bulkTargetFiles]);

  const confirmBulkArchive = useCallback(() => {
    confirmDialogRef.current?.close();
    if (pendingBulkIds.length === 0) {
      return;
    }
    archiveMutation.mutate(pendingBulkIds, {
      onError: handleArchiveError,
      onSuccess: handleArchiveSuccess,
    });
    setPendingBulkIds([]);
  }, [
    archiveMutation,
    handleArchiveError,
    handleArchiveSuccess,
    pendingBulkIds,
  ]);

  const cancelBulkArchive = useCallback(() => {
    confirmDialogRef.current?.close();
    setPendingBulkIds([]);
  }, []);

  const handleUndelete = useCallback(() => {
    if (recentlyDeletedIds.length === 0) {
      return;
    }
    const ids = recentlyDeletedIds;
    undeleteMutation.mutate(ids, {
      onError: (error) => {
        const message =
          error instanceof Error ? error.message : 'Failed to restore files.';
        setArchiveError(message);
      },
      onSuccess: () => {
        setRecentlyDeletedIds((prev) => prev.filter((id) => !ids.includes(id)));
        setHiddenIds((prev) => {
          const next = new Set(prev);
          for (const id of ids) {
            next.delete(id);
          }
          return next;
        });
      },
    });
  }, [recentlyDeletedIds, undeleteMutation]);

  return (
    <div className="card bg-base-100 shadow">
      <div className="card-body">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            {activeUploadCount > 0 ? (
              <span className="badge badge-info badge-outline">
                Uploading {activeUploadCount}
              </span>
            ) : null}
          </div>
        </div>

        <div className="flex justify-between flex-wrap items-center gap-3">
          <label className="sr-only" htmlFor="pdf-file-filter">
            Filter by filename
          </label>
          <input
            className="input input-bordered input-sm w-full max-w-xs"
            id="pdf-file-filter"
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter by filename…"
            type="search"
            value={filter}
          />

          <div className="flex items-center gap-2">
            {recentlyDeletedIds.length > 0 ? (
              <button
                className="btn btn-sm btn-outline"
                disabled={undeleteMutation.isPending}
                onClick={handleUndelete}
                type="button"
              >
                <ArrowUturnLeftIcon className="h-4 w-4" />
                {undeleteMutation.isPending
                  ? 'Restoring…'
                  : `Undo delete (${recentlyDeletedIds.length} file${
                      recentlyDeletedIds.length === 1 ? '' : 's'
                    })`}
              </button>
            ) : null}

            {bulkCount > 0 ? (
              <button
                className="btn btn-sm btn-outline btn-error"
                disabled={archiveMutation.isPending}
                onClick={openBulkConfirmation}
                type="button"
              >
                <TrashIcon className="h-4 w-4" />
                {archiveMutation.isPending
                  ? 'Deleting…'
                  : isFiltered
                    ? `Delete ${bulkCount} listed file${bulkCount === 1 ? '' : 's'}`
                    : `Delete all ${bulkCount} file${bulkCount === 1 ? '' : 's'}`}
              </button>
            ) : null}
          </div>
        </div>

        {archiveError ? (
          <div className="alert alert-error">
            <span>{archiveError}</span>
            <button
              className="btn btn-ghost btn-sm"
              onClick={() => setArchiveError(null)}
              type="button"
            >
              Dismiss
            </button>
          </div>
        ) : null}

        <div className="overflow-auto max-h-[60vh]">
          <table className="table readable-table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Filename</th>

                <th>Report</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {isError ? (
                <tr>
                  <td colSpan={4}>
                    <div className="alert alert-error">
                      <span>Failed to load your files.</span>
                    </div>
                  </td>
                </tr>
              ) : files === undefined ? (
                <tr>
                  <td className="text-base-content/70" colSpan={4}>
                    <div className="flex items-center gap-3">
                      <span className="loading loading-spinner loading-sm" />
                      <span>Loading…</span>
                    </div>
                  </td>
                </tr>
              ) : filteredFiles?.length === 0 ? (
                <tr>
                  <td className="text-base-content/80" colSpan={4}>
                    {filter ? 'No files match your filter.' : 'No files yet.'}
                  </td>
                </tr>
              ) : (
                (filteredFiles ?? []).map((file) => {
                  const upload = uploadsByFileId[file.fileId];
                  const reports = file.accessibilityReports ?? [];
                  const beforeReport = reports.find(
                    (r) => r.stage.toLowerCase() === 'before'
                  );
                  const afterReport = reports.find(
                    (r) => r.stage.toLowerCase() === 'after'
                  );
                  const beforeIssues = beforeReport?.issueCount;
                  const afterIssues = afterReport?.issueCount;
                  const fixedIssues =
                    typeof beforeIssues === 'number' &&
                    typeof afterIssues === 'number'
                      ? beforeIssues - afterIssues
                      : null;

                  return (
                    <tr key={file.fileId}>
                      {/* Status */}
                      <td>
                        <div className="space-y-2">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="badge badge-primary badge-soft">
                              {file.status}
                            </span>
                            {recentlyCompletedByFileId[file.fileId] ? (
                              <span className="badge badge-success badge-outline">
                                Just completed
                              </span>
                            ) : null}
                          </div>

                          {upload ? (
                            <progress className="progress progress-primary w-full" />
                          ) : null}
                        </div>
                      </td>

                      {/* File */}
                      <td>
                        {file.originalFileName}
                        <br />
                        <span className="text-xs text-base-content/75">
                          {formatBytes(file.sizeBytes)} •{' '}
                          {formatDateTime(file.createdAt)}
                        </span>
                      </td>

                      {/* Report */}
                      <td>
                        {file.status !== 'Completed' ? (
                          <span className="text-base-content/60">—</span>
                        ) : !afterReport ? (
                          <span className="text-base-content/60">
                            No report yet
                          </span>
                        ) : afterIssues === null ? (
                          <div className="text-xs text-base-content/70">
                            Report ready • After:{' '}
                            {formatDateTime(afterReport.generatedAt)}
                          </div>
                        ) : (
                          <div className="space-y-1">
                            <div className="flex flex-wrap items-center gap-2">
                              {afterIssues === 0 ? (
                                <span className="badge badge-success badge-sm">
                                  All checks passed
                                </span>
                              ) : (
                                <span className="badge badge-error badge-sm">
                                  {afterIssues} failing
                                </span>
                              )}
                              {typeof fixedIssues === 'number' &&
                              fixedIssues > 0 ? (
                                <span className="badge badge-success badge-outline badge-sm">
                                  Fixed {fixedIssues} Issues
                                </span>
                              ) : null}
                            </div>
                            <div className="text-xs text-base-content/70">
                              {typeof beforeIssues === 'number' ? (
                                <>
                                  Issues: {beforeIssues} → {afterIssues}
                                </>
                              ) : (
                                <>Issues remaining: {afterIssues}</>
                              )}{' '}
                              • After: {formatDateTime(afterReport.generatedAt)}
                            </div>
                          </div>
                        )}
                      </td>

                      {/* Actions */}
                      <td className="text-right">
                        <div className="flex flex-wrap justify-end gap-2">
                          {upload ? (
                            <button
                              className="btn btn-sm btn-outline btn-error"
                              disabled={!canCancelUpload(file.fileId)}
                              onClick={() => onCancelUpload(file.fileId)}
                              type="button"
                            >
                              Cancel upload
                            </button>
                          ) : null}

                          {file.status === 'Completed' ? (
                            <>
                              <Link
                                className="btn btn-sm btn-outline"
                                params={{ fileId: file.fileId }}
                                to="/reports/$fileId"
                              >
                                <DocumentChartBarIcon className="h-4 w-4" />
                                View Report
                              </Link>
                              <a
                                className="btn btn-sm btn-primary"
                                href={`/api/download/processed/${encodeURIComponent(
                                  file.fileId
                                )}`}
                                rel="noreferrer"
                                target="_blank"
                              >
                                <ArrowDownTrayIcon className="h-4 w-4" />
                                Download PDF
                              </a>
                            </>
                          ) : null}

                          <button
                            aria-label={`Delete ${file.originalFileName}`}
                            className="btn btn-sm btn-outline btn-error"
                            disabled={archiveMutation.isPending}
                            onClick={() => archiveSingle(file.fileId)}
                            title="Delete"
                            type="button"
                          >
                            <TrashIcon className="h-4 w-4" />
                          </button>
                        </div>
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Bulk archive confirmation dialog */}
      <dialog className="modal" ref={confirmDialogRef}>
        <div className="modal-box">
          <h3 className="font-bold text-lg">Confirm delete</h3>
          <p className="py-4">
            Are you sure you want to delete{' '}
            <strong>{pendingBulkIds.length}</strong> file
            {pendingBulkIds.length === 1 ? '' : 's'}? This action cannot be
            undone after you leave the page or refresh.
          </p>
          <div className="modal-action">
            <button
              className="btn btn-ghost"
              onClick={cancelBulkArchive}
              type="button"
            >
              Cancel
            </button>
            <button
              className="btn btn-error"
              onClick={confirmBulkArchive}
              type="button"
            >
              Delete
            </button>
          </div>
        </div>
        <form className="modal-backdrop" method="dialog">
          <button onClick={cancelBulkArchive} type="button">
            close
          </button>
        </form>
      </dialog>
    </div>
  );
}

import type { UserFile } from '@/queries/files.ts';
import type { UploadRow } from '@/lib/usePdfUploads.ts';
import { formatBytes, formatDateTime } from '@/lib/format.ts';
import { Link } from '@tanstack/react-router';

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
  return (
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
                <th>Status</th>
                <th>File</th>
                <th className="text-right">Size</th>
                <th>Created</th>
                <th className="text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {isError ? (
                <tr>
                  <td colSpan={5}>
                    <div className="alert alert-error">
                      <span>Failed to load your files.</span>
                    </div>
                  </td>
                </tr>
              ) : files === undefined ? (
                <tr>
                  <td className="text-base-content/70" colSpan={5}>
                    <div className="flex items-center gap-3">
                      <span className="loading loading-spinner loading-sm" />
                      <span>Loading…</span>
                    </div>
                  </td>
                </tr>
              ) : files.length === 0 ? (
                <tr>
                  <td className="text-base-content/60" colSpan={5}>
                    No files yet.
                  </td>
                </tr>
              ) : (
                files.map((file) => {
                  const upload = uploadsByFileId[file.fileId];

                  return (
                    <tr key={file.fileId}>
                      {/* Status */}
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
                          </div>

                          {upload ? (
                            <progress className="progress progress-primary w-full" />
                          ) : null}
                        </div>
                      </td>

                      {/* File */}
                      <td className="font-medium">{file.originalFileName}</td>

                      {/* Size */}
                      <td className="text-right">
                        {formatBytes(file.sizeBytes)}
                      </td>

                      {/* Created */}
                      <td>{formatDateTime(file.createdAt)}</td>

                      {/* Actions */}
                      <td className="text-right">
                        <div className="flex flex-wrap justify-end gap-2">
                          {upload ? (
                            <button
                              className="btn btn-xs btn-outline"
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
                                className="btn btn-xs btn-ghost"
                                params={{ fileId: file.fileId }}
                                to="/(authenticated)/pdf/$fileId/report"
                              >
                                View Report (TODO)
                              </Link>
                              <a
                                className="btn btn-xs btn-outline"
                                href={`/api/download/processed/${encodeURIComponent(
                                  file.fileId
                                )}`}
                                rel="noreferrer"
                                target="_blank"
                              >
                                Download PDF
                              </a>
                            </>
                          ) : null}
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
    </div>
  );
}

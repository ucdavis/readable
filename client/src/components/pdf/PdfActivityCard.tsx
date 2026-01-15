import type { UserFile } from '@/queries/files.ts';
import type { UploadRow } from '@/lib/usePdfUploads.ts';
import { formatBytes, formatDateTime } from '@/lib/format.ts';
import { Link } from '@tanstack/react-router';
import { ArrowDownTrayIcon } from '@heroicons/react/24/solid';
import { DocumentChartBarIcon } from '@heroicons/react/24/outline';

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
          <div className="flex items-center gap-2">
            {activeUploadCount > 0 ? (
              <span className="badge badge-info badge-outline">
                Uploading {activeUploadCount}
              </span>
            ) : null}
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="table">
            <thead>
              <tr>
                <th>Status</th>
                <th>Filename</th>
                
                <th>Report</th>
                <th className="text-right">Action</th>
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
              ) : files.length === 0 ? (
                <tr>
                  <td className="text-base-content/60" colSpan={4}>
                    No files yet.
                  </td>
                </tr>
              ) : (
                files.map((file) => {
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
                      <td>{file.originalFileName}
                        <br/>
                        <span className="text-xs text-base-content/75">{formatBytes(file.sizeBytes)} • {formatDateTime(file.createdAt)}</span>
                        
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
                          <div className="text-xs text-base-content/60">
                            Report ready • After: {formatDateTime(afterReport.generatedAt)}
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
                            <div className="text-xs text-base-content/60">
                              {typeof beforeIssues === 'number' ? (
                                <>Issues: {beforeIssues} → {afterIssues}</>
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
                                <ArrowDownTrayIcon className="h-4 w-4"/>
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

import { createFileRoute } from '@tanstack/react-router';
import { useMyFilesQuery } from '../../queries/files.ts';

export const Route = createFileRoute('/(authenticated)/upload')({
  component: RouteComponent,
});

function RouteComponent() {
  const filesQuery = useMyFilesQuery();

  return (
    <div className="container mx-auto max-w-5xl p-4 space-y-8">
      <div className="card bg-base-100 shadow">
        <div className="card-body">
          <h1 className="card-title">Upload</h1>
          <input
            className="file-input file-input-bordered w-full"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (!file) {
                return;
              }
              console.log('Selected file:', file);
            }}
            type="file"
          />
          <p className="text-sm text-base-content/70">
            Upload is not wired up yet; selecting a file just logs it.
          </p>
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

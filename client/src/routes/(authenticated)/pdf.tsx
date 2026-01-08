import { createFileRoute } from '@tanstack/react-router';
import {
  type ChangeEventHandler,
  type DragEventHandler,
  useState,
} from 'react';

export const Route = createFileRoute('/(authenticated)/pdf')({
  component: RouteComponent,
});

type ActivityRow = {
  addedAt: number;
  fileName: string;
  id: string;
  sizeBytes: number;
  status: 'Queued';
};

function RouteComponent() {
  const [isDragging, setIsDragging] = useState(false);
  const [activity, setActivity] = useState<ActivityRow[]>([]);
  const recentActivity = activity.slice(0, 25);

  const addFilesToActivity = (files: File[]) => {
    const pdfs = files.filter(looksLikePdf);
    const nonPdfs = files.filter((f) => !looksLikePdf(f));
    console.log('Selected PDFs:', pdfs);
    if (nonPdfs.length > 0) {
      console.log('Ignored non-PDF files:', nonPdfs);
    }

    setActivity((prev) => {
      const next: ActivityRow[] = pdfs.map((file) => ({
        addedAt: Date.now(),
        fileName: file.name,
        id: `${file.name}-${file.size}-${file.lastModified}`,
        sizeBytes: file.size,
        status: 'Queued',
      }));
      return [...next, ...prev];
    });
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
    addFilesToActivity(Array.from(e.dataTransfer.files ?? []));
  };

  const handleFileInput: ChangeEventHandler<HTMLInputElement> = (e) => {
    addFilesToActivity(Array.from(e.target.files ?? []));
    e.target.value = '';
  };

  return (
    <div className="min-h-screen bg-linear-to-br from-base-100 to-base-200">
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
        </div>

        <div className="card bg-base-100 shadow">
          <div className="card-body">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="card-title">Activity</h2>
              <button
                className="btn btn-sm btn-outline"
                onClick={() => setActivity([])}
                type="button"
              >
                Clear
              </button>
            </div>

            <div className="overflow-x-auto">
              <table className="table table-zebra">
                <thead>
                  <tr>
                    <th>File</th>
                    <th>Status</th>
                    <th className="text-right">Size</th>
                    <th>Added</th>
                  </tr>
                </thead>
                <tbody>
                  {recentActivity.length === 0 ? (
                    <tr>
                      <td className="text-base-content/60" colSpan={4}>
                        No activity yet. Drop a PDF above to see it here.
                      </td>
                    </tr>
                  ) : (
                    recentActivity.map((row) => (
                      <tr key={row.id}>
                        <td className="font-medium">{row.fileName}</td>
                        <td>
                          <span className="badge badge-ghost">
                            {row.status}
                          </span>
                        </td>
                        <td className="text-right">
                          {formatBytes(row.sizeBytes)}
                        </td>
                        <td>{formatTime(row.addedAt)}</td>
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

function formatTime(ms: number) {
  const d = new Date(ms);
  return Number.isNaN(d.valueOf()) ? String(ms) : d.toLocaleString();
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

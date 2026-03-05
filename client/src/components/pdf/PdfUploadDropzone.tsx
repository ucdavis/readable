import {
  type ChangeEventHandler,
  type DragEventHandler,
  useState,
} from 'react';
import { ArrowUpTrayIcon } from '@heroicons/react/24/solid';
import { Link } from '@tanstack/react-router';

export type PdfUploadDropzoneProps = {
  isUploading: boolean;
  onFilesSelected: (files: File[]) => void;
};

export function PdfUploadDropzone({
  isUploading,
  onFilesSelected,
}: PdfUploadDropzoneProps) {
  const inputId = 'file-upload';
  const [isDragging, setIsDragging] = useState(false);

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
    onFilesSelected(files);
  };

  const handleFileInput: ChangeEventHandler<HTMLInputElement> = (e) => {
    const files = Array.from(e.target.files ?? []);
    onFilesSelected(files);
    e.target.value = '';
  };

  return (
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
        <ArrowUpTrayIcon className="h-6 w-6" />
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
        id={inputId}
        multiple
        onChange={handleFileInput}
        type="file"
      />
      <label
        className="btn btn-primary inline-flex items-center gap-2"
        htmlFor={inputId}
      >
        Select Files
      </label>
      <div className="mt-3 text-xs text-base-content/70">
        PDF only · Multiple files supported
      </div>
      <p className="mt-2 max-w-sm text-xs text-base-content/60">
        Best results come from text-based PDFs. Scanned, handwritten, or
        image-only content may need manual transcription first.{' '}
        <Link className="link link-hover" hash="pdf-fit" to="/FAQs">
          Learn what works best.
        </Link>
      </p>
      {isUploading ? (
        <div className="mt-4 flex items-center gap-3 text-sm text-base-content/70">
          <span className="loading loading-spinner loading-sm" />
          <span>Uploading…</span>
        </div>
      ) : null}
    </div>
  );
}

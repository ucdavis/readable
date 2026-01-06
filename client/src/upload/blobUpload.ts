import { BlockBlobClient } from '@azure/storage-blob';

export type UploadProgress = {
  loadedBytes: number;
  percent: number;
  totalBytes: number;
};

function getMessage(err: unknown): string {
  if (err instanceof Error) {
    return err.message;
  }
  if (typeof err === 'string') {
    return err;
  }
  if (err && typeof err === 'object' && 'message' in err) {
    const message = (err as { message?: unknown }).message;
    if (typeof message === 'string') {
      return message;
    }
  }
  return String(err);
}

function getName(err: unknown): string {
  if (!err || typeof err !== 'object') {
    return '';
  }
  const name = (err as { name?: unknown }).name;
  return typeof name === 'string' ? name : '';
}

function getStatusCode(err: unknown): number | undefined {
  if (!err || typeof err !== 'object') {
    return undefined;
  }
  const anyErr = err as { status?: unknown; statusCode?: unknown };
  if (typeof anyErr.statusCode === 'number') {
    return anyErr.statusCode;
  }
  if (typeof anyErr.status === 'number') {
    return anyErr.status;
  }
  return undefined;
}

export function isLikelyTransient(err: unknown) {
  const status = getStatusCode(err);
  if (status && [408, 429, 500, 502, 503, 504].includes(status)) {
    return true;
  }
  const message = getMessage(err);
  return /network|timeout|econnreset|enotfound/i.test(message);
}

export function isLikelySasAuthError(err: unknown) {
  const status = getStatusCode(err);
  if (status === 403) {
    return true;
  }
  const message = getMessage(err);
  return /403|authorizationfailure|authenticationfailed|signature/i.test(
    message
  );
}

export function isAbortError(err: unknown) {
  if (getName(err) === 'AbortError') {
    return true;
  }
  const message = getMessage(err);
  return /aborted|abort/i.test(message);
}

export async function uploadToBlobWithSas(opts: {
  file: File;
  onProgress?: (p: UploadProgress) => void;
  sasUrl: string;
  signal?: AbortSignal;
}) {
  const { file, onProgress, sasUrl, signal } = opts;

  const client = new BlockBlobClient(sasUrl);

  const blockSize = 4 * 1024 * 1024; // 4MB
  const concurrency = 4;

  await client.uploadData(file, {
    abortSignal: signal,
    blobHTTPHeaders: file.type ? { blobContentType: file.type } : undefined,
    blockSize,
    concurrency,
    onProgress: (event) => {
      if (!onProgress) {
        return;
      }
      const loadedBytes = event.loadedBytes;
      const totalBytes = file.size;
      const percent = totalBytes
        ? Math.round((loadedBytes / totalBytes) * 100)
        : 0;
      onProgress({ loadedBytes, percent, totalBytes });
    },
  });
}

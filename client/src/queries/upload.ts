import { fetchJson } from '../lib/api.ts';

export type CreateUploadSasRequest = {
  contentType: string;
  originalFileName: string;
  sizeBytes: number;
};

export type CreateUploadSasResponse = {
  blobName: string;
  blobUrl: string;
  containerName: string;
  expiresAt: string;
  fileId: string;
  uploadUrl: string;
};

export async function createUploadSas(
  request: CreateUploadSasRequest,
  signal?: AbortSignal
): Promise<CreateUploadSasResponse> {
  return await fetchJson<CreateUploadSasResponse>('/api/upload/sas', {
    body: JSON.stringify(request),
    method: 'POST',
  }, signal);
}

export async function refreshUploadSas(
  fileId: string,
  signal?: AbortSignal
): Promise<CreateUploadSasResponse> {
  return await fetchJson<CreateUploadSasResponse>(
    `/api/upload/${encodeURIComponent(fileId)}/sas`,
    { method: 'POST' },
    signal
  );
}

export type SasResponse = {
  blobUrl: string;
  sasUrl: string;
  uploadId: string;
};

export async function getUploadSas(
  file: File,
  signal?: AbortSignal
): Promise<SasResponse> {
  const response = await createUploadSas(
    {
      contentType: file.type || 'application/pdf',
      originalFileName: file.name,
      sizeBytes: file.size,
    },
    signal
  );

  return {
    blobUrl: response.blobUrl,
    sasUrl: response.uploadUrl,
    uploadId: response.fileId,
  };
}

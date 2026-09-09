const defaultMaxUploadPages = 100;

const configuredMaxUploadPages = import.meta.env.VITE_MAX_UPLOAD_PAGES;
const parsedMaxUploadPages =
  typeof configuredMaxUploadPages === 'string' &&
  /^\d+$/.test(configuredMaxUploadPages)
    ? Number.parseInt(configuredMaxUploadPages, 10)
    : Number.NaN;

export const maxUploadPages =
  Number.isFinite(parsedMaxUploadPages) && parsedMaxUploadPages >= 0
    ? parsedMaxUploadPages
    : defaultMaxUploadPages;

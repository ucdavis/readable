const defaultMaxUploadPages = 100;

const configuredMaxUploadPages = Number(import.meta.env.VITE_MAX_UPLOAD_PAGES);

export const maxUploadPages =
  Number.isFinite(configuredMaxUploadPages) && configuredMaxUploadPages >= 0
    ? configuredMaxUploadPages
    : defaultMaxUploadPages;

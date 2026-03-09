/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_STATUS_BANNER?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

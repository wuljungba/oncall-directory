/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Microsoft Entra ID SPA client ID (PKCE flow, no secret) */
  readonly VITE_AZURE_CLIENT_ID: string

  /** Set to "true" to bypass Entra ID auth during local development */
  readonly VITE_DEV_AUTH?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

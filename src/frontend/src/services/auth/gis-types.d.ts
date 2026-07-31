/**
 * Type declarations for Google Identity Services (GIS) globals.
 * These types are for the `google.accounts.id` and `google.accounts.oauth2` APIs
 * which are loaded by the GoogleOAuthProvider component from @react-oauth/google.
 */

interface IdConfiguration {
  client_id: string
  callback: (response: CredentialResponse) => void
  auto_select?: boolean
  cancel_on_tap_outside?: boolean
  context?: 'signin' | 'signup' | 'use'
  state_cookie_domain?: string
  native_callback?: (response: CredentialResponse) => void
  native_login_uri?: string
  login_uri?: string
  prompt_parent_id?: string
  nonce?: string
  log_level?: 'set' | 'alert' | 'log' | 'warn'
  skippable?: boolean
  intermediate_iframe_close_callback?: () => void
  ux_mode?: 'popup' | 'redirect'
  allowed_parent_origin?: string | string[]
}

interface CredentialResponse {
  credential: string
  select_by?:
    | 'auto'
    | 'user'
    | 'user_1tap'
    | 'user_2tap'
    | 'btn'
    | 'btn_confirm'
    | 'btn_add_session'
    | 'btn_confirm_add_session'
}

interface PromptMomentNotification {
  isDisplayMoment: () => boolean
  isDisplayed: () => boolean
  isNotDisplayed: () => boolean
  getNotDisplayedReason: () =>
    | 'browser_not_supported'
    | 'invalid_client'
    | 'missing_client_id'
    | 'opt_out_or_no_session'
    | 'secure_http_required'
    | 'suppressed_by_user'
    | 'unregistered_origin'
    | 'unknown_reason'
  isSkippedMoment: () => boolean
  getSkippedReason: () =>
    | 'auto_cancel'
    | 'user_cancel'
    | 'tap_outside'
    | 'issuing_failed'
    | 'credential_not_found'
    | 'unknown_reason'
  isDismissedMoment: () => boolean
  getDismissedReason: () =>
    | 'credential_returned'
    | 'cancel_called'
    | 'flow_restarted'
    | 'unknown_reason'
  getMomentType: () => string
}

interface GsiButtonConfiguration {
  type: 'standard' | 'icon'
  shape?: 'rectangular' | 'pill' | 'circle' | 'square'
  theme?: 'outline' | 'filled_blue' | 'filled_black'
  size?: 'large' | 'medium' | 'small'
  text?: 'signin_with' | 'signup_with' | 'continue_with' | 'signin'
  logo_alignment?: 'left' | 'center'
  width?: string
  locale?: string
}

interface RevocationResponse {
  successful: boolean
  error?: string
}

declare namespace google.accounts.id {
  function initialize(config: IdConfiguration): void
  function prompt(momentListener?: (notification: PromptMomentNotification) => void): void
  function renderButton(parent: HTMLElement, options: GsiButtonConfiguration): void
  function disableAutoSelect(): void
  function storeCredential(credential: string, callback?: () => void): void
  function cancel(): void
  function revoke(hint: string, callback?: (response: RevocationResponse) => void): void
  let onGoogleLibraryLoad: (() => void) | undefined
}

declare namespace google.accounts.oauth2 {
  interface TokenClientConfig {
    client_id: string
    scope: string
    callback: (response: TokenResponse) => void
    error_callback?: (error: { type: string; message: string; stack?: string }) => void
    prompt_parent_id?: string
    /** Empty string attempts a silent token request (no popup). */
    prompt?: string
    include_granted_scopes?: boolean
    enable_serial_consent?: boolean
    hint?: string
    state?: string
  }

  interface TokenResponse {
    access_token: string
    expires_in: number
    scope: string
    token_type: string
    id_token?: string
    error?: string
    error_description?: string
    error_uri?: string
  }

  function initTokenClient(config: TokenClientConfig): TokenClient
  function revoke(token: string, callback?: (response: RevocationResponse) => void): void
  function hasGrantedAllScopes(tokenResponse: TokenResponse, scopes: string[]): boolean
  function hasGrantedAnyScope(tokenResponse: TokenResponse, scopes: string[]): boolean

  interface TokenClient {
    requestAccessToken: (overrideConfig?: Partial<TokenClientConfig>) => void
  }
}

// No Window augmentation needed — the declare namespace above makes
// `google.accounts.id` and `google.accounts.oauth2` globally available.

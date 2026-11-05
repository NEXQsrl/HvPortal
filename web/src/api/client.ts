// Minimal typed fetch client.
//  - same-origin only, cookies are HttpOnly (never readable from JS);
//  - every state-changing request carries the anti-forgery token (X-CSRF-TOKEN), refreshed on demand;
//  - 403 "reauthentication_required" opens the re-authentication dialog and retries once.

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryAfter?: number) {
    super(message);
  }
}

type ReauthHandler = () => Promise<boolean>;
let csrfToken: string | null = null;
let reauthHandler: ReauthHandler | null = null;
let unauthenticatedHandler: (() => void) | null = null;

export function onReauthRequired(handler: ReauthHandler) { reauthHandler = handler; }
export function onUnauthenticated(handler: () => void) { unauthenticatedHandler = handler; }
export function resetCsrf() { csrfToken = null; }

async function ensureCsrf(): Promise<string> {
  if (csrfToken) return csrfToken;
  const r = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin', cache: 'no-store' });
  const body = (await r.json()) as { token: string };
  csrfToken = body.token;
  return csrfToken;
}

async function parseError(r: Response): Promise<ApiError> {
  let code = 'http_' + r.status;
  let message = r.statusText;
  try {
    const body = await r.json();
    code = body.code ?? body.title ?? code;
    message = body.detail ?? body.title ?? message;
  } catch { /* not JSON */ }
  const retry = Number(r.headers.get('Retry-After'));
  return new ApiError(r.status, code, message, Number.isFinite(retry) && retry > 0 ? retry : undefined);
}

export async function api<T>(method: string, path: string, body?: unknown, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const unsafe = method !== 'GET' && method !== 'HEAD';
  if (unsafe) headers['X-CSRF-TOKEN'] = await ensureCsrf();
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const r = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
    cache: 'no-store',
  });

  if (r.ok) {
    if (r.status === 204) return undefined as T;
    const text = await r.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  const err = await parseError(r);
  if (!retried && err.code === 'csrf_invalid') {
    resetCsrf();
    return api<T>(method, path, body, true);
  }
  if (!retried && err.code === 'reauthentication_required' && reauthHandler) {
    if (await reauthHandler()) {
      resetCsrf();
      return api<T>(method, path, body, true);
    }
  }
  if (err.status === 401 && err.code === 'unauthenticated') unauthenticatedHandler?.();
  throw err;
}

export const get = <T>(path: string) => api<T>('GET', path);
export const post = <T>(path: string, body?: unknown) => api<T>('POST', path, body ?? {});
export const put = <T>(path: string, body?: unknown) => api<T>('PUT', path, body ?? {});
export const patch = <T>(path: string, body?: unknown) => api<T>('PATCH', path, body ?? {});
export const del = <T>(path: string) => api<T>('DELETE', path);

/** Stable idempotency key per user action (prevents duplicate jobs on double click / retry). */
export function newIdempotencyKey(): string {
  return 'ui-' + crypto.randomUUID();
}

/**
 * Raw upload (application/octet-stream) with progress, for the host file explorer. XMLHttpRequest because fetch has
 * no upload progress. Same CSRF rules as api(); returns an abort function.
 */
export function uploadFile<T>(path: string, file: Blob, onProgress: (loaded: number, total: number) => void): { promise: Promise<T>; abort: () => void } {
  const xhr = new XMLHttpRequest();
  const promise = (async () => {
    const send = (token: string) => new Promise<{ status: number; text: string }>((resolve, reject) => {
      xhr.open('POST', path);
      xhr.withCredentials = true;
      xhr.setRequestHeader('X-CSRF-TOKEN', token);
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.setRequestHeader('Accept', 'application/json');
      xhr.upload.onprogress = (e) => onProgress(e.loaded, e.lengthComputable ? e.total : file.size);
      xhr.onload = () => resolve({ status: xhr.status, text: xhr.responseText });
      xhr.onerror = () => reject(new ApiError(0, 'network_error', 'Network error'));
      xhr.onabort = () => reject(new ApiError(0, 'aborted', 'Upload cancelled'));
      xhr.send(file);
    });
    let r = await send(await ensureCsrf());
    if (r.status === 400 && r.text.includes('csrf_invalid')) {
      resetCsrf();
      r = await send(await ensureCsrf());
    }
    if (r.status >= 200 && r.status < 300) return (r.text ? JSON.parse(r.text) : undefined) as T;
    let code = 'http_' + r.status;
    let message = String(r.status);
    try {
      const body = JSON.parse(r.text);
      code = body.code ?? body.title ?? code;
      message = body.detail ?? body.title ?? message;
    } catch { /* not JSON */ }
    throw new ApiError(r.status, code, message);
  })();
  return { promise, abort: () => xhr.abort() };
}

/**
 * State-changing request with a non-JSON body or response (backup download / restore upload). Same CSRF and
 * re-authentication handling as api(); returns the raw Response when it is OK.
 */
export async function rawRequest(method: string, path: string, body: BodyInit, headers: Record<string, string>, retried = false): Promise<Response> {
  const r = await fetch(path, {
    method,
    headers: { ...headers, 'X-CSRF-TOKEN': await ensureCsrf() },
    body,
    credentials: 'same-origin',
    cache: 'no-store',
  });
  if (r.ok) return r;
  const err = await parseError(r);
  if (!retried && err.code === 'csrf_invalid') {
    resetCsrf();
    return rawRequest(method, path, body, headers, true);
  }
  if (!retried && err.code === 'reauthentication_required' && reauthHandler) {
    if (await reauthHandler()) {
      resetCsrf();
      return rawRequest(method, path, body, headers, true);
    }
  }
  throw err;
}

// Thin fetch wrapper for the NetWatch REST API (same origin only).

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly code?: string,
  ) {
    super(message);
  }
}

type UnauthorizedHandler = (code?: string) => void;
let onUnauthorized: UnauthorizedHandler = () => {};

/** Registers the callback used when the session expired (401) or a password change is required. */
export function setUnauthorizedHandler(handler: UnauthorizedHandler) {
  onUnauthorized = handler;
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  let payload: BodyInit | undefined;
  if (body instanceof FormData) {
    payload = body;
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body);
  }
  if (method !== 'GET') {
    // Required by the server's CSRF protection.
    headers['X-NetWatch-Request'] = '1';
  }

  const response = await fetch(url, { method, headers, body: payload, credentials: 'same-origin' });
  if (response.status === 204 || response.status === 202) {
    return undefined as T;
  }

  const isJson = response.headers.get('content-type')?.includes('json');
  const data = isJson ? await response.json().catch(() => null) : null;
  if (!response.ok) {
    const message = (data && (data.error || data.title)) || `Request failed (${response.status})`;
    const code = data?.code as string | undefined;
    if (response.status === 401 && !url.startsWith('/api/auth/login')) {
      onUnauthorized();
    } else if (response.status === 403 && code === 'password_change_required') {
      onUnauthorized(code);
    }
    throw new ApiError(response.status, message, code);
  }
  return data as T;
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body?: unknown) => request<T>('PUT', url, body ?? {}),
  del: <T>(url: string) => request<T>('DELETE', url),
  upload: <T>(url: string, form: FormData) => request<T>('POST', url, form),
};

/** Builds a query string, skipping empty values. */
export function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}

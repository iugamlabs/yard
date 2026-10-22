import type { paths, components } from './generated';
export type { paths, components } from './generated';
export type User = components['schemas']['UserView'];
export interface Organization {
  id: string;
  name: string;
  ownerId: string;
  archived: boolean;
}
let token = '';
export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
  ) {
    super(code);
  }
}
export async function csrf() {
  const r = await fetch('/api/auth/csrf', { credentials: 'same-origin' });
  token = (await r.json()).token;
  return token;
}
export async function api<T = any>(path: string, method = 'GET', body?: unknown): Promise<T> {
  if (method !== 'GET' && !token) await csrf();
  const r = await fetch('/api' + path, {
    method,
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      ...(method === 'GET' ? {} : { 'X-CSRF-TOKEN': token }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (r.status === 401) token = '';
  if (!r.ok) {
    let code = r.statusText;
    try {
      code = (await r.json()).errorCode ?? code;
    } catch {
      /* HTTP without JSON */
    }
    throw new ApiError(r.status, code);
  }
  if (
    ['/auth/login', '/auth/logout', '/auth/register', '/auth/external/registration'].includes(path)
  )
    token = '';
  return r.status === 204 ? (undefined as T) : r.json();
}
export async function externalForm(action: 'login' | 'link', password = '') {
  const t = await csrf();
  const f = document.createElement('form');
  f.method = 'POST';
  f.action = '/api/auth/external/github/' + action;
  for (const [name, value] of Object.entries({ __RequestVerificationToken: t, password })) {
    const i = document.createElement('input');
    i.type = 'hidden';
    i.name = name;
    i.value = value;
    f.append(i);
  }
  document.body.append(f);
  f.submit();
}

// Requests derive their payload from the exported server contract. Low-level api<T>
// remains available for business modules while their endpoints are being added.
export const identity = {
  me: () => api<User>('/auth/me'),
  register: (body: components['schemas']['RegisterBody']) => api('/auth/register', 'POST', body),
  login: (body: components['schemas']['LoginBody']) =>
    api<{ mustChangePassword: boolean }>('/auth/login', 'POST', body),
  changePassword: (body: components['schemas']['PasswordBody']) =>
    api<void>('/auth/change-password', 'POST', body),
  requestEmail: (body: components['schemas']['EmailRequestBody']) =>
    api<{ challengeId: string; expiresIn: number; retryAfter: number }>(
      '/auth/email/request',
      'POST',
      body,
    ),
  verifyEmail: (body: components['schemas']['EmailVerifyBody']) =>
    api<{ verified: boolean; signInAgain: boolean }>('/auth/email/verify', 'POST', body),
};
type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete';
type Operation<P extends keyof paths, M extends HttpMethod> = M extends keyof paths[P]
  ? NonNullable<paths[P][M]>
  : never;
type Body<O> = O extends { requestBody: { content: { 'application/json': infer T } } } ? T : never;
export async function contractRequest<P extends keyof paths, M extends HttpMethod>(
  path: P,
  method: M,
  options: { body?: Body<Operation<P, M>>; path?: Record<string, string> } = {},
): Promise<unknown> {
  let url: string = path;
  for (const [key, value] of Object.entries(options.path ?? {}))
    url = url.replace('{' + key + '}', encodeURIComponent(value));
  if (url.includes('{')) throw new Error('Missing API path parameter');
  return api(url.replace(/^\/api/, ''), method.toUpperCase(), options.body);
}

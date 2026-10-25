import type { components } from './generated';
export type { paths, components } from './generated';
export type Note = components['schemas']['NoteView'];
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}
export async function request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const response = await fetch('/api' + path, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    let message = response.statusText;
    try {
      const problem = await response.json();
      message =
        Object.values(problem.errors ?? {})
          .flat()
          .join(' ') ||
        problem.detail ||
        problem.title ||
        message;
    } catch {
      /* Non-JSON server error. */
    }
    throw new ApiError(response.status, message);
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
export const notes = {
  list: () => request<Note[]>('/notes/'),
  create: (body: components['schemas']['CreateNote']) => request<Note>('/notes/', 'POST', body),
  delete: (id: Note['id']) => request<void>('/notes/' + encodeURIComponent(id), 'DELETE'),
};

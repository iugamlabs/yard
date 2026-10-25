import { afterEach, expect, it, vi } from 'vitest';
import { notes, ApiError } from '@yard/api-client';
afterEach(() => vi.unstubAllGlobals());
it('sends the API contract payload and reads a created note', async () => {
  const fetch = vi
    .fn()
    .mockResolvedValue(
      new Response(JSON.stringify({ id: 7, text: 'Persist me' }), { status: 201 }),
    );
  vi.stubGlobal('fetch', fetch);
  expect(await notes.create({ text: 'Persist me' })).toEqual({ id: 7, text: 'Persist me' });
  expect(fetch).toHaveBeenCalledWith(
    '/api/notes/',
    expect.objectContaining({ method: 'POST', body: JSON.stringify({ text: 'Persist me' }) }),
  );
});
it('surfaces server validation messages', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValue(
        new Response(
          JSON.stringify({ errors: { text: ['Enter between 1 and 200 characters.'] } }),
          { status: 400 },
        ),
      ),
  );
  await expect(notes.create({ text: '' })).rejects.toThrow('Enter between 1 and 200 characters.');
});
it('handles empty delete responses', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })));
  await expect(notes.delete(7)).resolves.toBeUndefined();
});
it('preserves failure status for callers', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValue(new Response('unavailable', { status: 503, statusText: 'Unavailable' })),
  );
  await expect(notes.list()).rejects.toMatchObject({
    status: 503,
    message: 'Unavailable',
    constructor: ApiError,
  });
});

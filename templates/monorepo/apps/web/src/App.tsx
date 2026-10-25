import React, { useEffect, useState } from 'react';
import { notes, type Note } from '@yard/api-client';
import { Button } from '@yard/ui/button';
import { Input } from '@yard/ui/input';
export default function App() {
  const [items, setItems] = useState<Note[]>([]);
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    notes
      .list()
      .then(setItems)
      .catch((e) => setError(String(e.message)));
  }, []);
  async function change(action: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try {
      await action();
      setItems(await notes.list());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Request failed');
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="mx-auto max-w-2xl space-y-6 p-8">
      <header>
        <h1 className="text-3xl font-semibold">YardApp</h1>
        <p>Notes — a small example module you can replace.</p>
      </header>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void change(async () => {
            await notes.create({ text });
            setText('');
          });
        }}
      >
        <Input aria-label="Note text" value={text} onChange={(e) => setText(e.target.value)} />
        <Button disabled={busy} type="submit">
          Add note
        </Button>
      </form>
      {error && <p role="alert">{error}</p>}
      <ul className="space-y-3">
        {items.map((note) => (
          <li key={note.id} className="flex items-center justify-between gap-4 rounded border p-3">
            <span>{note.text}</span>
            <Button
              disabled={busy}
              variant="outline"
              onClick={() => void change(() => notes.delete(note.id))}
            >
              Delete
            </Button>
          </li>
        ))}
      </ul>
      {!items.length && <p>No notes yet.</p>}
    </main>
  );
}

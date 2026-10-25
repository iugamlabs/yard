using Microsoft.EntityFrameworkCore;
using YardApp.Api.Infrastructure;

namespace YardApp.Api.Modules.Notes;

public sealed class NoteOperations(AppDbContext db) {
    public Task<NoteView[]> List(CancellationToken ct) =>
        db
            .Notes.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new NoteView(x.Id, x.Text))
            .ToArrayAsync(ct);

    public async Task<NoteView> Create(string text, CancellationToken ct) {
        var note = new Note { Text = text };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return new(note.Id, note.Text);
    }

    public async Task<bool> Delete(int id, CancellationToken ct) =>
        await db.Notes.Where(x => x.Id == id).ExecuteDeleteAsync(ct) != 0;
}

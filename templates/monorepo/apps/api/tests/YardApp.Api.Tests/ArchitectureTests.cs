using Microsoft.EntityFrameworkCore;
using Xunit;
using YardApp.Api.Infrastructure;
using YardApp.Api.Modules.Notes;

public class ArchitectureTests {
    [Fact]
    public void ModelMatchesMigrations() {
        using var db = new AppDbContextFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task NotesPersistAndDelete() {
        await using var db = new AppDbContextFactory().CreateDbContext([]);
        var notes = new NoteOperations(db);
        var created = await notes.Create("Example note", CancellationToken.None);
        Assert.Contains(
            await notes.List(CancellationToken.None),
            x => x.Id == created.Id && x.Text == "Example note"
        );
        Assert.True(await notes.Delete(created.Id, CancellationToken.None));
        Assert.False(await notes.Delete(created.Id, CancellationToken.None));
    }
}

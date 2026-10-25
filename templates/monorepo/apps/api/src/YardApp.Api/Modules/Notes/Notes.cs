using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace YardApp.Api.Modules.Notes;

public sealed class Note {
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note> {
    public void Configure(EntityTypeBuilder<Note> entity) {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Text).HasMaxLength(200).IsRequired();
    }
}

public sealed record CreateNote(string Text);

public sealed record NoteView(int Id, string Text);

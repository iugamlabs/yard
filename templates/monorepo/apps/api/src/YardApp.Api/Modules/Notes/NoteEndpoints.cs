namespace YardApp.Api.Modules.Notes;

public static class NoteEndpoints {
    public static IEndpointRouteBuilder MapNotes(this IEndpointRouteBuilder endpoints) {
        var group = endpoints.MapGroup("/api/notes").WithTags("Notes");
        group.MapGet("/", (NoteOperations notes, CancellationToken ct) => notes.List(ct));
        group
            .MapPost(
                "/",
                async (CreateNote body, NoteOperations notes, CancellationToken ct) => {
                    var text = body.Text?.Trim() ?? "";
                    if (text.Length is 0 or > 200)
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]> {
                                ["text"] = ["Enter between 1 and 200 characters."],
                            }
                        );
                    var note = await notes.Create(text, ct);
                    return Results.Created($"/api/notes/{note.Id}", note);
                }
            )
            .Produces<NoteView>(201)
            .ProducesValidationProblem();
        group.MapDelete(
            "/{id:int}",
            async (int id, NoteOperations notes, CancellationToken ct) =>
                await notes.Delete(id, ct) ? Results.NoContent() : Results.NotFound()
        );
        return endpoints;
    }
}

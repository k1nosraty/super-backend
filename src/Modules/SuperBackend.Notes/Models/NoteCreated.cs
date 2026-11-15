using System.Text.Json.Serialization;

namespace SuperBackend.Notes;

internal sealed record NoteCreated([property: JsonPropertyName("ownerId")] string OwnerId)
{
    public const string JobType = "notes.created";
}

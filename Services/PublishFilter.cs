using Tatami.Models;

namespace Tatami.Services;

public static class PublishFilter
{
    public static bool IsPublic(Note note) => note.Metadata.Publish;
}

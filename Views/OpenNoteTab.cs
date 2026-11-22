using AvaloniaEdit.Document;
using Tatami.Services;

namespace Tatami.Views;

internal sealed class OpenNoteTab(EditorBuffer buffer)
{
    public EditorBuffer Buffer { get; } = buffer;
    public TextDocument Document { get; } = new TextDocument(buffer.Text);
    public int CaretOffset { get; set; }
    public string Path => Buffer.Path;
    public bool IsDirty => Buffer.IsDirty;

    public void ReloadDocument(string text)
    {
        Document.Text = text;
        CaretOffset = 0;
    }
}

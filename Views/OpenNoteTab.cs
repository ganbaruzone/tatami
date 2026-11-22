using System.IO;
using AvaloniaEdit.Document;
using Tatami.Services;

namespace Tatami.Views;

internal sealed class OpenNoteTab
{
    public EditorBuffer Buffer { get; }
    public TextDocument Document { get; }
    public int CaretOffset { get; set; }
    public string Path => Buffer.Path;
    public bool IsDirty => Buffer.IsDirty;

    public OpenNoteTab(EditorBuffer buffer)
    {
        Buffer = buffer;
        Document = new TextDocument(buffer.Text);
    }

    public void ReloadDocument(string text)
    {
        Document.Text = text;
        CaretOffset = 0;
    }
}

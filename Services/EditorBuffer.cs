using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tatami.Services;

// Independent of the editor widget so save/conflict behavior can be tested without a UI.
public sealed class EditorBuffer
{
    private byte[] _savedBytes;
    private string _savedText;
    private readonly Encoding _encoding;
    public string Path { get; private set; }
    public string Text { get; set; }
    public bool IsDirty => Text != _savedText;
    public string EncodingName => _encoding.WebName.ToUpperInvariant();

    private EditorBuffer(string path, byte[] bytes, string text, Encoding encoding)
    { Path = path; _savedBytes = bytes; _savedText = Text = text; _encoding = encoding; }

    public static async Task<EditorBuffer> OpenAsync(string path)
    {
        PathSafety.RejectLinks(path);
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var text = await reader.ReadToEndAsync();
        return new EditorBuffer(path, bytes, text, reader.CurrentEncoding);
    }

    public void Rename(string workspace, string name) => Path = new NoteFileService(workspace).Rename(Path, name);

    public async Task SaveAsync()
    {
        PathSafety.RejectLinks(Path);
        var current = await File.ReadAllBytesAsync(Path);
        if (!current.SequenceEqual(_savedBytes))
            throw new IOException("ファイルが外部で変更されています。編集内容をコピーして退避し、ファイルを開き直してください。上書きは行っていません。");
        var text = Text;
        var bytes = _encoding.GetPreamble().Concat(_encoding.GetBytes(text)).ToArray();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $".tatami-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(Path));
            File.Move(temporary, Path, true);
            _savedBytes = bytes;
            _savedText = text;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

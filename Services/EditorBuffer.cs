using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Tatami.Services;

// Independent of the editor widget so save/conflict behavior can be tested without a UI.
public sealed class EditorBuffer
{
    private string _savedText;
    private string _savedHash;
    private bool _recoveredDirty;
    private Encoding _encoding;
    public string Path { get; private set; }
    public string Text { get; set; }
    public bool IsDirty => _recoveredDirty || Text != _savedText;
    public string EncodingName => _encoding.WebName.ToUpperInvariant();
    public string SavedHash => _savedHash;

    public async Task<(bool Exists, string Hash, string? Text)> ReadDiskSnapshotAsync()
    {
        if (!File.Exists(Path)) return (false, "", null);
        PathSafety.RejectLinks(Path);
        var bytes = await File.ReadAllBytesAsync(Path);
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return (true, Hash(bytes), await reader.ReadToEndAsync());
    }

    public async Task ReloadFromDiskAsync()
    {
        var opened = await OpenAsync(Path);
        _savedText = Text = opened.Text;
        _savedHash = opened._savedHash;
        _encoding = opened._encoding;
        _recoveredDirty = false;
    }

    private EditorBuffer(string path, byte[] bytes, string text, Encoding encoding)
    { Path = path; _savedText = Text = text; _savedHash = Hash(bytes); _encoding = encoding; }

    public void RestoreBackup(string text, string baselineHash, bool wasDirty)
    {
        if (baselineHash.Length != 64 || !baselineHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("退避内容のベースラインが不正です。");
        Text = text;
        _savedHash = baselineHash.ToUpperInvariant();
        _recoveredDirty = wasDirty;
    }

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
        if (!Hash(current).Equals(_savedHash, StringComparison.Ordinal))
            throw new IOException("ファイルが外部で変更されています。編集内容をコピーして退避し、ファイルを開き直してください。上書きは行っていません。");
        var text = Text;
        var bytes = _encoding.GetPreamble().Concat(_encoding.GetBytes(text)).ToArray();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, $".tatami-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(Path));
            File.Move(temporary, Path, true);
            _savedText = text;
            _savedHash = Hash(bytes);
            _recoveredDirty = false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

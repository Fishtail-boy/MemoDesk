using System.Text;

namespace MemoDesk.Vault.Core;

/// <summary>有限大小的 RFC 4180 CSV。没有日志、网络、原文存储或备忘依赖。</summary>
public sealed class VaultCsv : IDisposable
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public string[] Headers { get; }
    private readonly List<string[]> _rows;
    public int Count => _rows.Count;
    private VaultCsv(string[] headers, List<string[]> rows) { Headers = headers; _rows = rows; }

    public static VaultCsv Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new VaultException(VaultError.TooLarge);
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        var read = 0;
        void EndField()
        {
            row.Add(field.ToString()); field.Clear(); closed = false;
            if (row.Count > 64) throw new VaultException(VaultError.TooLarge);
        }
        void EndRow()
        {
            EndField();
            if (row.Any(v => v.Length != 0)) rows.Add(row.ToArray());
            row.Clear();
            if (rows.Count > 10001) throw new VaultException(VaultError.TooLarge);
        }
        try
        {
            int next;
            while ((next = reader.Read()) >= 0)
            {
                if (++read > MaxFileBytes) throw new VaultException(VaultError.TooLarge);
                var c = (char)next;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                        else { quoted = false; closed = true; }
                    }
                    else field.Append(c);
                }
                else if (c == ',' ) EndField();
                else if (c is '\r' or '\n') { if (c == '\r' && reader.Peek() == '\n') reader.Read(); EndRow(); }
                else if (c == '"' && field.Length == 0 && !closed) quoted = true;
                else if (closed || c == '"') throw new VaultException(VaultError.InvalidData);
                else field.Append(c);
                if (field.Length > 65536) throw new VaultException(VaultError.TooLarge);
            }
            if (quoted) throw new VaultException(VaultError.InvalidData);
            if (field.Length != 0 || row.Count != 0 || closed) EndRow();
            if (rows.Count < 2 || rows[0].Any(string.IsNullOrWhiteSpace)) throw new VaultException(VaultError.InvalidData);
            var headers = rows[0]; rows.RemoveAt(0);
            if (rows.Any(r => r.Length != headers.Length)) throw new VaultException(VaultError.InvalidData);
            return new VaultCsv(headers, rows);
        }
        catch { foreach (var r in rows) Array.Clear(r); throw; }
        finally { field.Clear(); row.Clear(); }
    }

    public int FindColumn(params string[] names) => Array.FindIndex(Headers,
        h => names.Contains(h.Trim(), StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<VaultEntryDraft> Map(int title, int userName, int password, int url, int notes, int category)
    {
        if (title < 0 || password < 0 || new[] { title, userName, password, url, notes, category }.Any(i => i >= Headers.Length))
            throw new VaultException(VaultError.InvalidMapping);
        if (new[] { title, userName, url, category }.Any(i => i >= 0 && (i == password || i == notes)))
            throw new VaultException(VaultError.InvalidMapping);
        var result = new List<VaultEntryDraft>();
        try
        {
            foreach (var row in _rows)
            {
                string Get(int i) => i < 0 ? string.Empty : row[i];
                var draft = new VaultEntryDraft
                {
                    Title = Get(title), UserName = Get(userName), Password = Get(password), Url = Get(url),
                    Notes = Get(notes), Category = category < 0 ? "个人" : Get(category)
                };
                result.Add(draft); draft.Validate();
            }
            return result;
        }
        catch { foreach (var e in result) e.Dispose(); throw; }
    }
    public void Dispose() { Array.Clear(Headers); foreach (var row in _rows) Array.Clear(row); _rows.Clear(); }
    public override string ToString() => "VaultCsv:[redacted]";
}

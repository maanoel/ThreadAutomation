using System.Globalization;
using System.IO;

namespace ThreadOps.Services;

/// <summary>
/// Quem já foi seguido pelo app, em seguidos.log ao lado do executável
/// (uma linha por perfil: "AAAA-MM-DD HH:mm:ss&lt;TAB&gt;usuario").
/// Evita repetir perfis e alimenta o contador do limite diário.
/// </summary>
public sealed class FollowHistory
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly Dictionary<string, DateTime> _entries = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath { get; }

    /// <param name="fileName">seguidos.log para quem foi seguido; deixou-de-seguir.log para o inverso.</param>
    public FollowHistory(string fileName = "seguidos.log")
    {
        FilePath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(FilePath)) return;
        foreach (var line in File.ReadLines(FilePath))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2 &&
                DateTime.TryParseExact(parts[0], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                _entries[parts[1].Trim()] = when;
        }
    }

    public bool Contains(string handle) => _entries.ContainsKey(handle);

    public DateTime? When(string handle) => _entries.TryGetValue(handle, out var d) ? d : null;

    public int CountToday() => _entries.Values.Count(d => d.Date == DateTime.Today);

    public void Add(string handle)
    {
        var now = DateTime.Now;
        _entries[handle] = now;
        File.AppendAllText(FilePath, $"{now.ToString(DateFormat, CultureInfo.InvariantCulture)}\t{handle}{Environment.NewLine}");
    }
}

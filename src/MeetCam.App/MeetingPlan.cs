using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeetCam;

internal sealed class MeetingPlan
{
    private static readonly Regex MeetingCode = new("^[a-z]{3}-[a-z]{4}-[a-z]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Link { get; set; } = "";
    public DateTime LocalStart { get; set; }
    public bool Armed { get; set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetCam",
        "meeting.json");

    public static bool TryNormalize(string raw, out string url)
    {
        url = "";
        raw = raw.Trim().Trim('"');
        if (raw.Length == 0)
            return false;

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw["http://".Length..];
        if (MeetingCode.IsMatch(raw))
            raw = "https://meet.google.com/" + raw;
        else if (raw.StartsWith("meet.google.com/", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.Host.Equals("meet.google.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath.Trim('/');
        var head = path.Split('/', 2)[0];
        var lookup = path.StartsWith("lookup/", StringComparison.OrdinalIgnoreCase) && path.Length > "lookup/".Length;
        if (!lookup && !MeetingCode.IsMatch(head))
            return false;

        url = "https://meet.google.com/" + path + uri.Query;
        return true;
    }

    public static MeetingPlan? TryLoad()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            return JsonSerializer.Deserialize<MeetingPlan>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

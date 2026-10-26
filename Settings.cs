using System.Globalization;
using Microsoft.Win32;

namespace CodexAutoApprove;

public sealed record AppSettings(double IntervalSeconds, bool StartWithWindows, bool MinimizeToTray, double MinimumScore,
    double CooldownSeconds, double[] Scales, Dictionary<string, Point> ClickPoints)
{
    public static readonly double[] DefaultScales = [.5, .625, .75, .8, .9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    public static AppSettings Load(string path)
    {
        var ini = IniFile.Read(path);
        double Number(string section, string key, double fallback, double min, double max)
        {
            string? raw = ini.GetValueOrDefault(section)?.GetValueOrDefault(key);
            if (raw is null) return fallback;
            if (!double.TryParse(raw, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < min || n > max)
                throw new FormatException($"{section}.{key} must be between {min} and {max}.");
            return n;
        }
        var general = ini.GetValueOrDefault("General");
        bool startup = false;
        string? startupRaw = general?.GetValueOrDefault("StartWithWindows");
        if (startupRaw is not null && !bool.TryParse(startupRaw, out startup))
            throw new FormatException("General.StartWithWindows must be true or false.");
        bool minimizeToTray = true;
        string? trayRaw = general?.GetValueOrDefault("MinimizeToTray");
        if (trayRaw is not null && !bool.TryParse(trayRaw, out minimizeToTray))
            throw new FormatException("General.MinimizeToTray must be true or false.");
        string? scaleRaw = ini.GetValueOrDefault("Matching")?.GetValueOrDefault("Scales");
        var scales = scaleRaw is null ? DefaultScales : scaleRaw.Split(',').Select(s =>
        {
            if (!double.TryParse(s.Trim(), CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < .25 || n > 4)
                throw new FormatException("Matching.Scales must contain comma-separated numbers between 0.25 and 4.");
            return n;
        }).Distinct().ToArray();
        var points = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase)
        {
            ["allow_once.png"] = new(163, 23), ["allow_once_2.png"] = new(155, 55),
            ["allow_once_3.png"] = new(130, 17)
        };
        foreach (var (section, values) in ini.Where(p => p.Key.StartsWith("Template:", StringComparison.OrdinalIgnoreCase)))
        {
            if (!int.TryParse(values.GetValueOrDefault("ClickX"), out int x) || !int.TryParse(values.GetValueOrDefault("ClickY"), out int y))
                throw new FormatException($"{section} needs integer ClickX and ClickY coordinates.");
            points[section[9..]] = new(x, y);
        }
        return new(Number("General", "CheckIntervalSeconds", 2, .2, 3600), startup, minimizeToTray,
            Number("Matching", "MinimumScore", .90, .85, 1), Number("Matching", "ClickCooldownSeconds", 3, .5, 3600), scales, points);
    }

    public static void SaveGeneral(string path, double interval, bool startup, bool? minimizeToTray = null)
    {
        var ini = IniFile.Read(path);
        if (!ini.TryGetValue("General", out var general)) ini["General"] = general = new(StringComparer.OrdinalIgnoreCase);
        general["CheckIntervalSeconds"] = interval.ToString(CultureInfo.InvariantCulture);
        general["StartWithWindows"] = startup ? "true" : "false";
        if (minimizeToTray.HasValue) general["MinimizeToTray"] = minimizeToTray.Value ? "true" : "false";
        IniFile.Write(path, ini);
    }
}

internal static class IniFile
{
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return sections;
        string section = "General";
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int equals = line.IndexOf('=');
            if (equals <= 0) throw new FormatException($"Invalid settings.ini line: {line}");
            if (!sections.TryGetValue(section, out var values)) sections[section] = values = new(StringComparer.OrdinalIgnoreCase);
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return sections;
    }

    public static void Write(string path, Dictionary<string, Dictionary<string, string>> sections)
    {
        string text = "; Interval is in seconds. Changes are reloaded automatically.\r\n" +
            string.Join("\r\n", sections.Select(s => $"[{s.Key}]\r\n" + string.Join("\r\n", s.Value.Select(v => $"{v.Key}={v.Value}")) + "\r\n"));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class WindowsStartup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexAutoApprove";

    public static void Synchronize(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the application executable.");
            string command = $"\"{executable}\" --background";
            if (!Equals(key.GetValue(ValueName), command)) key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
    }
}

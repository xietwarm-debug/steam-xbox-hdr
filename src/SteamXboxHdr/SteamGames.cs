using System.Text.RegularExpressions;

namespace SteamXboxHdr;

public record SteamGame(string AppId, string Directory);

public static class SteamGames
{
    static IEnumerable<string> Values(string text, string key) => Regex.Matches(text,
        "\"" + Regex.Escape(key) + "\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)
        .Select(m => m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\""));

    public static List<SteamGame> Read(string steamRoot)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamRoot) };
        var config = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(config))
            foreach (var value in Values(File.ReadAllText(config), "path")) libraries.Add(Path.GetFullPath(value));
        var result = new List<SteamGame>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!System.IO.Directory.Exists(apps)) continue;
            var common = Path.GetFullPath(Path.Combine(apps, "common")) + Path.DirectorySeparatorChar;
            foreach (var manifest in System.IO.Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var id = Path.GetFileNameWithoutExtension(manifest)[12..];
                if (!ulong.TryParse(id, out _)) continue;
                var install = Values(File.ReadAllText(manifest), "installdir").FirstOrDefault();
                if (string.IsNullOrWhiteSpace(install)) continue;
                var directory = Path.GetFullPath(Path.Combine(common, install)).TrimEnd(Path.DirectorySeparatorChar);
                if (directory.StartsWith(common, StringComparison.OrdinalIgnoreCase)) result.Add(new(id, directory));
            }
        }
        return result;
    }
    public static SteamGame? Find(IEnumerable<SteamGame> games, string executable) => games
        .Where(g => Path.GetFullPath(executable).StartsWith(g.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(g => g.Directory.Length).FirstOrDefault();
}

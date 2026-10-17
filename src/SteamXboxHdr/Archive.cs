using System.Security.Cryptography;
using System.Text.Json;

namespace SteamXboxHdr;

public record SteamShot(string Path, string Account, string Game);
public record ShotPair(SteamShot Steam, string Hdr, string? Sdr);
public record CaptureContext(DateTimeOffset Started, nint Window, uint ProcessId, string ProcessName, HdrState Display);
public record ArchivedFile(string Source, string Destination, long Bytes, string Sha256);

public static class ShotFiles
{
    public const string ArchiveMarker = "__XboxHDR__";
    public static SteamShot? Steam(string steamRoot, string path)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetFullPath(steamRoot), "userdata");
        var parts = System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(path)).Split(System.IO.Path.DirectorySeparatorChar);
        if (parts.Length != 6 || parts[1] != "760" || parts[2] != "remote" || parts[4] != "screenshots" ||
            !parts[0].All(char.IsAsciiDigit) || parts[0].Length == 0 || !ulong.TryParse(parts[3], out _) ||
            !new[] { ".jpg", ".jpeg", ".png" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()) ||
            System.IO.Path.GetFileNameWithoutExtension(path).Contains(ArchiveMarker, StringComparison.OrdinalIgnoreCase)) return null;
        return new SteamShot(System.IO.Path.GetFullPath(path), parts[0], parts[3]);
    }

    public static List<string> Scan(string steamRoot, string captures)
    {
        var paths = new List<string>();
        var users = System.IO.Path.Combine(steamRoot, "userdata");
        if (Directory.Exists(users))
            foreach (var account in Directory.GetDirectories(users))
            {
                var remote = System.IO.Path.Combine(account, "760", "remote");
                if (!Directory.Exists(remote)) continue;
                foreach (var game in Directory.GetDirectories(remote))
                {
                    var dir = System.IO.Path.Combine(game, "screenshots");
                    if (Directory.Exists(dir)) paths.AddRange(Directory.GetFiles(dir).Where(p => Steam(steamRoot, p) != null));
                }
            }
        if (Directory.Exists(captures)) paths.AddRange(Directory.GetFiles(captures).Where(p =>
            new[] { ".jxr", ".png" }.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())));
        return paths;
    }

    public static ShotPair Match(string steamRoot, string captures, IEnumerable<string> newFiles)
    {
        var files = newFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var steam = files.Select(p => Steam(steamRoot, p)).OfType<SteamShot>().ToArray();
        var hdr = files.Where(p => string.Equals(System.IO.Path.GetDirectoryName(p), System.IO.Path.GetFullPath(captures).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetExtension(p).Equals(".jxr", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (steam.Length != 1 || hdr.Length != 1)
            throw new InvalidOperationException($"无法唯一配对：Steam 新截图 {steam.Length} 张，Game Bar JXR {hdr.Length} 张。原文件均已保留。");
        var sdr = files.SingleOrDefault(p => System.IO.Path.GetExtension(p).Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(System.IO.Path.ChangeExtension(p, null), System.IO.Path.ChangeExtension(hdr[0], null), StringComparison.OrdinalIgnoreCase));
        return new ShotPair(steam[0], hdr[0], sdr);
    }

    public static void NoReparsePoints(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("路径包含目录链接，已停止归档：" + dir.FullName);
    }
}

public static class ArchiveWriter
{
    public static string Write(ShotPair pair, CaptureContext context, bool includeSdr, Action<string> validateHdr, bool manual = false)
    {
        validateHdr(pair.Hdr);
        var folder = System.IO.Path.GetDirectoryName(pair.Steam.Path)!;
        ShotFiles.NoReparsePoints(folder);
        Directory.CreateDirectory(folder);
        var metadata = System.IO.Path.Combine(folder, ".metadata");
        ShotFiles.NoReparsePoints(metadata);
        Directory.CreateDirectory(metadata);
        File.SetAttributes(metadata, File.GetAttributes(metadata) | FileAttributes.Hidden);
        var id = Guid.NewGuid().ToString("N");
        var stem = System.IO.Path.GetFileNameWithoutExtension(manual ? pair.Hdr : pair.Steam.Path) + ShotFiles.ArchiveMarker + id;
        var created = new List<string>();
        var records = new List<ArchivedFile>();
        try
        {
            foreach (var source in new[] { pair.Hdr, includeSdr ? pair.Sdr : null }.OfType<string>())
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("源图片是文件链接。");
                ShotFiles.NoReparsePoints(System.IO.Path.GetDirectoryName(source)!);
                var destination = System.IO.Path.Combine(folder, stem + System.IO.Path.GetExtension(source).ToLowerInvariant());
                var staging = destination + ".partial";
                created.Add(staging);
                string hash;
                long length;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    length = input.Length;
                    if (length == 0) throw new IOException("源图片尚未写入完成。");
                    input.CopyTo(output);
                    input.Position = 0;
                    hash = Convert.ToHexString(SHA256.HashData(input));
                    output.Flush(true);
                    output.Position = 0;
                    if (output.Length != length || Convert.ToHexString(SHA256.HashData(output)) != hash)
                        throw new IOException("复制校验失败。");
                }
                File.Move(staging, destination, false);
                created.Add(destination);
                records.Add(new(source, destination, length, hash));
            }
            var manifest = System.IO.Path.Combine(metadata, stem + ".json");
            var temporaryManifest = manifest + ".partial";
            created.Add(temporaryManifest);
            File.WriteAllText(temporaryManifest, JsonSerializer.Serialize(new
            {
                schema = 1, requestId = id, context.Started, archived = DateTimeOffset.UtcNow,
                steam = pair.Steam, context.ProcessId, context.ProcessName, context.Display,
                hdrValidation = "WIC decoded floating-point JPEG XR; no tone mapping", files = records,
                pairing = manual ? "Manual game-folder archive; reference Steam screenshot is a directory reference only, not a temporal match"
                    : "Unique new files within request interval; not guaranteed to be the same frame"
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryManifest, manifest, false);
            return folder;
        }
        catch
        {
            foreach (var path in created) { try { File.Delete(path); } catch (IOException) { } }
            throw;
        }
    }
}

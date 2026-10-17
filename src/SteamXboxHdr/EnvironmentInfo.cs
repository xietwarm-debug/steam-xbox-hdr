using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SteamXboxHdr;

public sealed record Settings(string SteamPath = "", string CapturePath = "", int ScreenshotKey = 123, bool IncludeSdr = true)
{
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamXboxHdr");
    public static Settings Load()
    {
        var path = Path.Combine(DataFolder, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataFolder);
        var path = Path.Combine(DataFolder, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public record EnvironmentReport(string SteamPath, string CapturePath, string GameBar, bool CaptureNotBlocked, string CaptureStatus, int AccountCount);

public static class EnvironmentProbe
{
    static string? packageVersion;
    static bool packageChecked;
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);
    static object? Read(RegistryKey root, string key, string value)
    {
        using var subkey = root.OpenSubKey(key);
        return subkey?.GetValue(value);
    }
    public static EnvironmentReport Inspect(Settings settings)
    {
        var steam = string.IsNullOrWhiteSpace(settings.SteamPath)
            ? Read(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") as string ?? ""
            : settings.SteamPath;
        if (steam.Length > 0) steam = Path.GetFullPath(steam);
        var captures = settings.CapturePath;
        if (string.IsNullOrWhiteSpace(captures))
        {
            var id = new Guid("EDC0FE71-98D8-4F4A-B920-C8DC133CB165");
            if (SHGetKnownFolderPath(ref id, 0x4000, 0, out var pointer) == 0)
            {
                try { captures = Marshal.PtrToStringUni(pointer) ?? ""; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
            if (string.IsNullOrWhiteSpace(captures)) captures = Read(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}") as string ?? "";
        }
        if (captures.Length > 0) captures = Path.GetFullPath(Environment.ExpandEnvironmentVariables(captures));
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\ActivatableClasses\Package");
        var package = packages?.GetSubKeyNames().FirstOrDefault(n => n.StartsWith("Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase));
        using var protocol = Registry.ClassesRoot.OpenSubKey("ms-gamebar");
        if (!packageChecked)
        {
            packageChecked = true;
            try
            {
                using var query = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name Microsoft.XboxGamingOverlay | Select-Object -First 1 -ExpandProperty Version\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (query != null && query.WaitForExit(6000))
                {
                    var result = query.StandardOutput.ReadToEnd().Trim();
                    if (query.ExitCode == 0 && Version.TryParse(result, out _)) packageVersion = result;
                }
                else query?.Kill();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        var gameBar = packageVersion != null ? "Microsoft.XboxGamingOverlay " + packageVersion : package ??
            (protocol != null ? "已注册 ms-gamebar 协议（版本未知）" : "未检测到 Xbox Game Bar");
        var blocked = Equals(Read(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR"), 0) ||
            Equals(Read(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"), 0) ||
            Equals(Read(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled"), 0);
        var users = Path.Combine(steam, "userdata");
        var accountCount = Directory.Exists(users) ? Directory.GetDirectories(users).Count(p => Path.GetFileName(p).All(char.IsAsciiDigit)) : 0;
        return new(steam, captures, gameBar, !blocked && (packageVersion != null || package != null || protocol != null),
            blocked ? "捕获被系统设置或策略禁用" : "未发现禁用项；实际捕获能力需在游戏内验证", accountCount);
    }
}

public static class HdrImage
{
    public static Guid NativePixelFormat(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 4;
        var directory = reader.ReadUInt32();
        if (directory < 8 || directory > stream.Length - 2) throw new InvalidDataException("JPEG XR 目录位置无效。");
        stream.Position = directory;
        var count = reader.ReadUInt16();
        if (directory + 2L + count * 12L > stream.Length) throw new InvalidDataException("JPEG XR 目录不完整。");
        for (var i = 0; i < count; i++)
        {
            stream.Position = directory + 2L + i * 12L;
            var tag = reader.ReadUInt16();
            var type = reader.ReadUInt16();
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            if (tag != 0xbc01) continue;
            if (type != 1 || length != 16 || offset > stream.Length - 16) throw new InvalidDataException("JPEG XR 像素格式字段无效。");
            stream.Position = offset;
            return new Guid(reader.ReadBytes(16));
        }
        throw new InvalidDataException("JPEG XR 缺少原生像素格式。");
    }

    public static void Validate(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4 || !signature.SequenceEqual(new byte[] { 0x49, 0x49, 0xbc, 0x01 }))
            throw new InvalidDataException("图片不是有效的 JPEG XR 文件，已停止归档。");
        var nativeFormat = NativePixelFormat(stream);
        // Native half-float WIC formats are valid HDR but WPF exposes some of them as PixelFormats.Default.
        var supported = new[] { "19", "1a", "1b", "3a", "3b", "42" }
            .Select(suffix => new Guid("6fddc324-4e03-4bfe-b185-3d77768dc9" + suffix))
            .Append(new Guid("e3fed78f-e8db-4acf-84c1-e97f6136b327"))
            .Append(new Guid("58ad26c2-c623-4d9d-b320-387e49f8c442"));
        if (!supported.Contains(nativeFormat)) throw new InvalidDataException($"JXR 原生像素格式 {nativeFormat} 未确认为浮点 HDR；保留原文件供检查。");
        stream.Position = 0;
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new InvalidDataException("JXR 图像尺寸无效。");
        var probe = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Rgba128Float, null, 0);
        probe.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), new float[4], 16, 0);
    }
}

public sealed class DiagnosticLog
{
    readonly object gate = new();
    public string Path { get; } = System.IO.Path.Combine(Settings.DataFolder, "logs", $"{DateTime.Now:yyyyMMdd}.log");
    public event Action<string>? Written;
    public void Write(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}";
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { line += "（日志无法写入磁盘）"; }
        Written?.Invoke(line);
    }
}

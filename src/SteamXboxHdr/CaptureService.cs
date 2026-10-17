namespace SteamXboxHdr;

public sealed class CaptureService : IDisposable
{
    readonly Settings settings;
    readonly DiagnosticLog log;
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    readonly List<SteamGame> games;
    Pending? pending;
    public string? LastArchive { get; private set; }
    public event Action<string>? Archived;
    sealed class Pending(CaptureContext context)
    {
        public CaptureContext Context = context;
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public bool Conflicted;
    }
    public CaptureService(Settings settings, DiagnosticLog log)
    {
        this.settings = settings;
        this.log = log;
        games = SteamGames.Read(settings.SteamPath);
        foreach (var path in ShotFiles.Scan(settings.SteamPath, settings.CapturePath)) known.Add(path);
        _ = Task.Run(Poll);
    }
    public async void Trigger(nint window, DateTimeOffset started)
    {
        Pending request;
        lock (gate)
        {
            if (pending != null)
            {
                pending.Conflicted = true;
                log.Write("截图间隔过短，取消本轮自动配对。Steam 按键继续正常传递；请等 6 秒后重试。");
                return;
            }
            request = new Pending(new(started, window, 0, "", new("", null, "待检测")));
            pending = request;
        }
        try
        {
            request.Context = Native.Context(window, started);
            if (request.Context.ProcessId == Environment.ProcessId) throw new InvalidOperationException("请切换到游戏后按截图键。");
            using var process = System.Diagnostics.Process.GetProcessById((int)request.Context.ProcessId);
            var executable = process.MainModule?.FileName ?? throw new InvalidOperationException("无法读取前台进程路径。");
            var game = SteamGames.Find(games, executable) ?? throw new InvalidOperationException("前台进程不在已识别的 Steam 游戏目录中，已跳过联动。");
            if (request.Context.Display.Enabled != true) throw new InvalidOperationException(request.Context.Display.Detail + "；本轮未触发 Game Bar。");
            // Give the Steam key a chance to reach the game before synthesizing a different shortcut.
            var releaseDeadline = DateTime.UtcNow.AddSeconds(2);
            while (Native.GetAsyncKeyState(settings.ScreenshotKey) < 0)
            {
                if (DateTime.UtcNow > releaseDeadline) throw new InvalidOperationException("截图键长按超过 2 秒，已跳过联动。");
                await Task.Delay(15, stop.Token);
            }
            if (Native.GetForegroundWindow() != window) throw new InvalidOperationException("前台窗口已改变，已跳过本轮捕获。");
            Native.SendGameBarScreenshot();
            log.Write($"已发送 Game Bar 截图快捷键；进程 {request.Context.ProcessName}，显示器 {request.Context.Display.Monitor}。等待新增图片。");
            await Task.Delay(TimeSpan.FromSeconds(6), stop.Token);
            string[] files;
            lock (gate)
            {
                if (request.Conflicted) throw new InvalidOperationException("本轮存在连续截图，无法唯一配对，原文件全部保留。");
                files = request.Files.ToArray();
            }
            var pair = ShotFiles.Match(settings.SteamPath, settings.CapturePath, files);
            if (pair.Steam.Game != game.AppId) throw new InvalidOperationException("Steam 截图游戏 ID 与前台游戏不一致，已取消归档。");
            // Polling observes a new path once; now require its size and timestamp to settle.
            var sizes = files.ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
            await Task.Delay(400, stop.Token);
            foreach (var path in new[] { pair.Steam.Path, pair.Hdr, settings.IncludeSdr ? pair.Sdr : null }.OfType<string>())
                if (sizes[path].Length == 0 || sizes[path] != (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))) throw new IOException("图片为空或仍在写入，已跳过本轮归档。");
            lock (gate)
                if (request.Conflicted || request.Files.Count != files.Length) throw new InvalidOperationException("等待期间出现更多图片，已取消配对。");
            log.Write("正在校验并复制到 Steam JPG 同一文件夹…");
            var folder = await Task.Run(() => ArchiveWriter.Write(pair, request.Context, settings.IncludeSdr, HdrImage.Validate));
            LastArchive = folder;
            log.Write($"归档成功：账号 {pair.Steam.Account}，游戏 {pair.Steam.Game}，目录 {folder}");
            Archived?.Invoke(folder);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Write("本轮跳过：" + e.Message); }
        finally { lock (gate) { if (ReferenceEquals(pending, request)) pending = null; } }
    }
    async Task Poll()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            var idleTicks = 0;
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                try
                {
                    bool active;
                    lock (gate) active = pending != null;
                    if (!active && ++idleTicks < 10) continue;
                    idleTicks = 0;
                    var paths = ShotFiles.Scan(settings.SteamPath, settings.CapturePath);
                    lock (gate)
                        foreach (var path in paths)
                            if (known.Add(path) && pending != null && File.GetLastWriteTimeUtc(path) >= pending.Context.Started.UtcDateTime)
                                pending.Files.Add(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    lock (gate) { if (pending != null) pending.Conflicted = true; }
                    log.Write("目录扫描失败：" + e.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); }
}

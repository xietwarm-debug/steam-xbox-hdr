using System.Diagnostics;
using System.Text.Json;

namespace SteamXboxHdr;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 3 && args[0] == "--archive")
        {
            try
            {
                var report = EnvironmentProbe.Inspect(Settings.Load());
                var shot = ShotFiles.Steam(report.SteamPath, args[1]) ?? throw new InvalidOperationException("目标不是有效的 Steam 游戏截图路径。");
                if (!File.Exists(shot.Path)) throw new FileNotFoundException("Steam 参考截图不存在。", shot.Path);
                var hdr = Path.GetFullPath(args[2]);
                var png = Path.ChangeExtension(hdr, ".png");
                var context = new CaptureContext(DateTimeOffset.UtcNow, 0, 0, "ManualArchive", new("未检测", null, "手动归档；不表示拍摄时的显示器状态"));
                var folder = ArchiveWriter.Write(new(shot, hdr, File.Exists(png) ? png : null), context, true, HdrImage.Validate, manual: true);
                new DiagnosticLog().Write("手动归档成功：" + hdr + " → " + folder);
            }
            catch (Exception e) { new DiagnosticLog().Write("手动归档失败：" + e.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            var report = EnvironmentProbe.Inspect(Settings.Load());
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { report, foregroundDisplay = Native.GetHdr(Native.GetForegroundWindow()), os = Environment.OSVersion.VersionString }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Length == 2 && args[0] == "--preview")
        {
            using var preview = new MainForm();
            preview.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(args[1]);
            return;
        }
        using var single = new Mutex(true, @"Local\SteamXboxHdr.Desktop", out var created);
        if (!created) { MessageBox.Show("助手已在运行，请查看系统托盘。", "Steam × Xbox HDR"); return; }
        try { Application.Run(new MainForm(args.Contains("--enable"))); }
        catch (Exception e) { MessageBox.Show(e.Message, "Steam × Xbox HDR 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

public sealed class MainForm : Form
{
    readonly TextBox steam = new() { Dock = DockStyle.Fill, PlaceholderText = "留空自动识别 Steam 安装目录" };
    readonly TextBox captures = new() { Dock = DockStyle.Fill, PlaceholderText = "留空读取 Windows 捕获目录" };
    readonly ComboBox hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly CheckBox includeSdr = new() { Text = "同时归档同名 PNG 副本", AutoSize = true, Checked = true };
    readonly TextBox environment = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.ControlLightLight };
    readonly TextBox output = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.ControlLightLight };
    readonly Button toggle = new() { Text = "启用截图联动", AutoSize = true };
    readonly Label state = new() { Text = "已暂停 · 启用后在游戏中按截图键", AutoSize = true, ForeColor = Color.FromArgb(40, 100, 95) };
    readonly DiagnosticLog log = new();
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem trayToggle;
    CaptureService? service;
    ScreenshotHook? hook;
    bool exiting;

    public MainForm(bool enableAtStart = false)
    {
        Text = "Steam × Xbox HDR 截图助手 · 0.1.1";
        Icon = SystemIcons.Application;
        Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(920, 780);
        MinimumSize = new Size(920, 780);
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 3, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        var title = new Label { Text = "一键截图，保留两份光影", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), AutoSize = true };
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 3);
        AddPath(layout, "Steam 目录", steam, 1);
        AddPath(layout, "Game Bar 捕获", captures, 2);
        layout.Controls.Add(new Label { Text = "Steam 截图键", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        for (int i = 1; i <= 24; i++) hotkey.Items.Add("F" + i);
        layout.Controls.Add(hotkey, 1, 3);
        layout.Controls.Add(includeSdr, 1, 4); layout.SetColumnSpan(includeSdr, 2);
        layout.Controls.Add(environment, 0, 5); layout.SetColumnSpan(environment, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var detect = new Button { Text = "重新检测", AutoSize = true };
        var save = new Button { Text = "保存设置", AutoSize = true };
        actions.Controls.AddRange([toggle, detect, save]);
        layout.Controls.Add(actions, 0, 6); layout.SetColumnSpan(actions, 3);
        layout.Controls.Add(state, 0, 7); layout.SetColumnSpan(state, 3);
        layout.Controls.Add(output, 0, 8); layout.SetColumnSpan(output, 3);
        var links = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        var logs = new Button { Text = "打开日志", AutoSize = true };
        var archives = new Button { Text = "最近归档", AutoSize = true };
        var hide = new Button { Text = "收起到托盘", AutoSize = true };
        links.Controls.AddRange([logs, archives, hide]);
        layout.Controls.Add(links, 0, 9); layout.SetColumnSpan(links, 3);
        Controls.Add(layout);
        var settings = Settings.Load();
        steam.Text = settings.SteamPath; captures.Text = settings.CapturePath;
        hotkey.SelectedIndex = Math.Clamp(settings.ScreenshotKey - (int)Keys.F1, 0, 23);
        includeSdr.Checked = settings.IncludeSdr;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => Restore());
        trayToggle = new ToolStripMenuItem("启用截图联动", null, (_, _) => Toggle());
        menu.Items.Add(trayToggle);
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Steam × Xbox HDR · 已暂停", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Restore();
        log.Written += line =>
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                if (output.TextLength > 32000) output.Clear();
                output.AppendText(line + Environment.NewLine);
            }));
        };
        toggle.Click += (_, _) => Toggle();
        detect.Click += (_, _) => Detect();
        save.Click += (_, _) => Guard(() => { Current().Save(); log.Write("设置已保存；修改后请重新启用联动。"); });
        logs.Click += (_, _) => Guard(() => { var dir = Path.Combine(Settings.DataFolder, "logs"); Directory.CreateDirectory(dir); Open(dir); });
        archives.Click += (_, _) => { if (service?.LastArchive is { } folder) Guard(() => Open(folder)); else log.Write("还没有成功归档的图片。"); };
        hide.Click += (_, _) => Hide();
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) =>
        {
            Detect();
            log.Write("Xbox 图片将直接复制到本次 Steam 截图所在的 screenshots 文件夹。截图键请与 Steam 设置保持一致；两套截图可能有帧延迟。");
            if (enableAtStart) Toggle();
            else log.Write("当前已暂停，请点击「启用截图联动」开始自动归档。");
        };
    }
    void AddPath(TableLayoutPanel layout, string label, TextBox textbox, int row)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(textbox, 1, row);
        var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill, AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = label, UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) textbox.Text = dialog.SelectedPath; };
        layout.Controls.Add(browse, 2, row);
    }
    Settings Current() => new(steam.Text.Trim(), captures.Text.Trim(), (int)Keys.F1 + hotkey.SelectedIndex, includeSdr.Checked);
    void Detect() => Guard(() =>
    {
        var report = EnvironmentProbe.Inspect(Current());
        environment.Text = $"Steam：{report.SteamPath}（{report.AccountCount} 个账号目录）\r\n捕获来源：{report.CapturePath}\r\nGame Bar：{report.GameBar}\r\n{report.CaptureStatus}\r\n归档到本次 Steam JPG 同一文件夹；按键时检测游戏 HDR。";
    });
    void Toggle() => Guard(() =>
    {
        if (service != null)
        {
            if (hook != null) hook.Enabled = false;
            service.Dispose(); service = null;
            toggle.Text = trayToggle.Text = "启用截图联动";
            tray.Text = "Steam × Xbox HDR · 已暂停";
            state.Text = "已暂停 · Steam 截图不受影响";
            SetInputs(true);
            log.Write("联动已暂停。");
            return;
        }
        var report = EnvironmentProbe.Inspect(Current());
        if (!Directory.Exists(Path.Combine(report.SteamPath, "userdata"))) throw new InvalidOperationException("未找到 Steam userdata，请检查 Steam 目录。");
        if (!Directory.Exists(report.CapturePath)) throw new InvalidOperationException("捕获目录不存在。请在 Game Bar 中确认实际保存位置，然后选择该目录。");
        if (!report.CaptureNotBlocked) throw new InvalidOperationException(report.CaptureStatus + "；" + report.GameBar);
        if (Path.GetFullPath(report.SteamPath).TrimEnd('\\').Equals(Path.GetFullPath(report.CapturePath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Steam 安装目录与捕获目录不能相同。");
        var settings = Current() with { SteamPath = report.SteamPath, CapturePath = report.CapturePath };
        hook ??= CreateHook();
        service = new CaptureService(settings, log);
        service.Archived += folder => { tray.ShowBalloonTip(4000, "HDR 截图已归档", folder, ToolTipIcon.Info); };
        hook.Key = settings.ScreenshotKey; hook.Enabled = true;
        toggle.Text = trayToggle.Text = "暂停截图联动";
        tray.Text = "Steam × Xbox HDR · 已启用";
        state.Text = $"已启用 · 在游戏中按 {hotkey.SelectedItem} · 每轮请间隔至少 7 秒";
        SetInputs(false);
        log.Write("联动已启用。按键始终透传；只复制实际捕获的浮点 JXR，不改 Steam 原文件。");
    });
    ScreenshotHook CreateHook()
    {
        var created = new ScreenshotHook();
        created.Pressed += (window, started) => BeginInvoke((Action)(() => service?.Trigger(window, started)));
        return created;
    }
    void SetInputs(bool enabled) { steam.Enabled = captures.Enabled = hotkey.Enabled = includeSdr.Enabled = enabled; }
    void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void Guard(Action action) { try { action(); } catch (Exception e) { log.Write(e.Message); MessageBox.Show(this, e.Message, "Steam × Xbox HDR", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    protected override void Dispose(bool disposing)
    {
        if (disposing) { service?.Dispose(); hook?.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}

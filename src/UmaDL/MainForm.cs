using UmaFs.Core;

namespace UmaDL;

/// <summary>下载器主窗口。纯代码布局，不依赖 designer 文件。</summary>
internal sealed class MainForm : Form
{
    private readonly ComboBox _groupBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly TextBox _destBox = new() { Width = 520 };
    private readonly NumericUpDown _parallelBox = new() { Minimum = 1, Maximum = 64, Value = 8, Width = 60 };
    private readonly Button _browseBtn = new() { Text = "…", Width = 30 };
    private readonly Button _checkBtn = new() { Text = "检查清单", Width = 96 };
    private readonly Button _startBtn = new() { Text = "开始下载", Width = 96 };
    private readonly Button _stopBtn = new() { Text = "停止", Width = 70, Enabled = false };
    private readonly ProgressBar _bar = new() { Width = 668, Height = 20, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
    private readonly Label _status = new() { AutoSize = true, Text = "就绪" };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Width = 668, Height = 280, Font = new Font("Consolas", 9), WordWrap = false
    };

    private CancellationTokenSource? _cts;
    private RemoteManifest? _manifest;

    private static readonly (string Label, string? Value)[] Groups =
    {
        ("DeleteOnLogin（登录即删，默认）", "DeleteOnLogin"),
        ("全部资源（--all）", null),
        ("DownloadLogin", "DownloadLogin"),
        ("DelayRelease（未发布）", "DelayRelease"),
        ("HomeLogin", "HomeLogin"),
        ("Tutorial", "Tutorial"),
        ("DataManagementTarget", "DataManagementTarget"),
        ("DeleteOnLoginWithConditionLive", "DeleteOnLoginWithConditionLive"),
        ("none（无标记）", "none"),
    };

    public MainForm()
    {
        Text = "UmaDL — 赛马娘资源下载器";
        ClientSize = new Size(700, 540);
        MinimumSize = new Size(700, 540);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var g in Groups) _groupBox.Items.Add(g.Label);
        _groupBox.SelectedIndex = 0;
        _destBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmaFs", "assets");

        var top = new FlowLayoutPanel { Left = 16, Top = 12, Width = 668, Height = 76, WrapContents = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        top.Controls.Add(new Label { Text = "分组", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_groupBox);
        top.Controls.Add(new Label { Text = "并发", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_parallelBox);

        var mid = new FlowLayoutPanel { Left = 16, Top = 92, Width = 668, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        mid.Controls.Add(new Label { Text = "目录", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        mid.Controls.Add(_destBox);
        mid.Controls.Add(_browseBtn);

        var buttons = new FlowLayoutPanel { Left = 16, Top = 130, Width = 668, Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        buttons.Controls.Add(_checkBtn);
        buttons.Controls.Add(_startBtn);
        buttons.Controls.Add(_stopBtn);

        _bar.Left = 16; _bar.Top = 172;
        _bar.Width = 668;
        _status.Left = 16; _status.Top = 198;
        int logH = ClientSize.Height - 222 - 40;
        _log.Left = 16; _log.Top = 222; _log.Width = 668; _log.Height = logH;
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.WordWrap = false;

        Controls.AddRange(new Control[] { top, mid, buttons, _bar, _status, _log });

        _browseBtn.Click += (_, _) => Browse();
        _checkBtn.Click += async (_, _) => await CheckAsync();
        _startBtn.Click += async (_, _) => await StartAsync();
        _stopBtn.Click += (_, _) => _cts?.Cancel();

        Append($"输出目录: {_destBox.Text}");
        Append("点「检查清单」查看待下载数量，或直接「开始下载」。");
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _destBox.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) _destBox.Text = dlg.SelectedPath;
    }

    private string? SelectedGroup =>
        _groupBox.SelectedIndex >= 0 && _groupBox.SelectedIndex < Groups.Length
            ? Groups[_groupBox.SelectedIndex].Value
            : "DeleteOnLogin";

    private async Task CheckAsync()
    {
        _checkBtn.Enabled = false;
        _status.Text = "读取清单…";
        _cts = new CancellationTokenSource();
        try
        {
            var ct = _cts.Token;
            _manifest = await Task.Run(() => RemoteManifest.FetchAsync(Append, ct), ct);
            Append($"清单就绪: {_manifest.Assets.Count} 条，失败分清单 {_manifest.Failures.Count}");

            var g = SelectedGroup;
            var sel = _manifest.Assets.Where(a => RemoteManifest.MatchesGroup(a, g)).ToList();
            var present = sel.Count(a => File.Exists(Path.Combine(_destBox.Text, a.Hash[..2], a.Hash)));
            long bytes = sel.Where(a => !File.Exists(Path.Combine(_destBox.Text, a.Hash[..2], a.Hash))).Sum(a => a.Size);
            Append($"分组 {g ?? "all"}: {sel.Count} 条，已有 {present}，待下载 {sel.Count - present}（{Cli.Human(bytes)}）");
            _status.Text = $"待下载 {sel.Count - present} 条 / {Cli.Human(bytes)}";
        }
        catch (Exception ex)
        {
            Append($"失败: {ex.Message}");
            _status.Text = "失败";
        }
        finally
        {
            _checkBtn.Enabled = true;
        }
    }

    private async Task StartAsync()
    {
        _startBtn.Enabled = false;
        _stopBtn.Enabled = true;
        _cts = new CancellationTokenSource();
        _bar.Value = 0;
        _status.Text = "下载中…";

        string dest = _destBox.Text;
        string? group = SelectedGroup;
        int parallel = (int)_parallelBox.Value;
        var ct = _cts.Token;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        IProgress<AssetDownloader.Progress> progress = new Progress<AssetDownloader.Progress>(p =>
        {
            if (p.Total > 0)
            {
                _bar.Maximum = p.Total;
                _bar.Value = Math.Min(p.Done, p.Total);
            }
            double speed = sw.Elapsed.TotalSeconds > 0 ? p.Bytes / sw.Elapsed.TotalSeconds : 0;
            _status.Text = $"{p.Done}/{p.Total}  下载 {p.Downloaded}  跳过 {p.Skipped}  失败 {p.Failed}" +
                           $"   {Cli.Human(p.Bytes)}  {Cli.Human((long)speed)}/s";
        });

        try
        {
            await Task.Run(() => Cli.Run(dest, group, parallel, 0, false, false, Append, p => progress.Report(p), ct), ct);
            _status.Text = "完成";
        }
        catch (OperationCanceledException)
        {
            Append("已停止。已下载的文件保留，下次会跳过。");
            _status.Text = "已停止";
        }
        catch (Exception ex)
        {
            Append($"失败: {ex.Message}");
            _status.Text = "失败";
        }
        finally
        {
            _startBtn.Enabled = true;
            _stopBtn.Enabled = false;
        }
    }

    private void Append(string line)
    {
        if (_log.InvokeRequired)
        {
            _log.BeginInvoke(() => Append(line));
            return;
        }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }
}
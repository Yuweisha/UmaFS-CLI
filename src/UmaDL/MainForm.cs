using UmaFs.Core;

namespace UmaDL;

/// <summary>下载器主窗口。纯代码布局，不依赖 designer 文件。</summary>
internal sealed class MainForm : Form
{
    private readonly ComboBox _groupBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly ComboBox _platBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 };
    private readonly ComboBox _layoutBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox _fullChk = new() { Text = "全量", Width = 60, Height = 22 };
    private readonly CheckBox _pruneChk = new() { Text = "清理旧文件", Width = 100, Height = 22 };
    private readonly TextBox _destBox = new() { Width = 520 };
    private readonly NumericUpDown _parallelBox = new() { Minimum = 1, Maximum = 64, Value = 8, Width = 60 };
    private readonly Button _browseBtn = new() { Text = "…", Width = 30 };
    private readonly TextBox _rvBox = new() { Width = 460 };
    private readonly TextBox _proxyBox = new() { Width = 300 };
    private readonly CheckBox _proxyChk = new() { Text = "使用代理", Width = 90, Height = 22, Checked = false };
    private readonly Button _proxyTestBtn = new() { Text = "测试", Width = 60 };
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
        ClientSize = new Size(700, 640);
        MinimumSize = new Size(700, 640);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;

        _platBox.Items.AddRange(new object[] { "PC", "Android", "iOS" });
        _platBox.SelectedIndex = 0;
        _layoutBox.Items.AddRange(new object[] { "CDN 目录树", "扁平目录" });
        _layoutBox.SelectedIndex = 0;

        foreach (var g in Groups) _groupBox.Items.Add(g.Label);
        _groupBox.SelectedIndex = 0;
        _destBox.Text = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var top = new FlowLayoutPanel { Left = 16, Top = 12, Width = 668, Height = 76, WrapContents = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        top.Controls.Add(new Label { Text = "分组", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_groupBox);
        top.Controls.Add(new Label { Text = "平台", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_platBox);
        top.Controls.Add(new Label { Text = "布局", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_layoutBox);
        top.Controls.Add(new Label { Text = "并发", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        top.Controls.Add(_parallelBox);
        top.Controls.Add(_fullChk);
        top.Controls.Add(_pruneChk);

        var mid = new FlowLayoutPanel { Left = 16, Top = 92, Width = 668, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        mid.Controls.Add(new Label { Text = "目录", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        mid.Controls.Add(_destBox);
        mid.Controls.Add(_browseBtn);

        var rvRow = new FlowLayoutPanel { Left = 16, Top = 130, Width = 668, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        rvRow.Controls.Add(new Label { Text = "资源代号", Width = 64, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        rvRow.Controls.Add(_rvBox);

        var proxyRow = new FlowLayoutPanel { Left = 16, Top = 168, Width = 668, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        proxyRow.Controls.Add(new Label { Text = "代理", Width = 34, Height = 22, TextAlign = ContentAlignment.MiddleLeft });
        proxyRow.Controls.Add(_proxyBox);
        proxyRow.Controls.Add(_proxyChk);
        proxyRow.Controls.Add(_proxyTestBtn);

        var buttons = new FlowLayoutPanel { Left = 16, Top = 206, Width = 668, Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        buttons.Controls.Add(_checkBtn);
        buttons.Controls.Add(_startBtn);
        buttons.Controls.Add(_stopBtn);

        _bar.Left = 16; _bar.Top = 250;
        _bar.Width = 668;
        _status.Left = 16; _status.Top = 278;
        int logH = ClientSize.Height - 306 - 40;
        _log.Left = 16; _log.Top = 306; _log.Width = 668; _log.Height = logH;
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.WordWrap = false;

        Controls.AddRange(new Control[] { top, mid, rvRow, proxyRow, buttons, _bar, _status, _log });

        _browseBtn.Click += (_, _) => Browse();
        _proxyTestBtn.Click += async (_, _) => await TestProxyAsync();
        _proxyChk.CheckedChanged += (_, _) => SyncProxyUi();
        _checkBtn.Click += async (_, _) => await CheckAsync();
        _startBtn.Click += async (_, _) => await StartAsync();
        _stopBtn.Click += (_, _) => _cts?.Cancel();

        // 环境里配了代理就自动带上，省得每次手填
        string? envProxy = UmaFs.Core.Net.ProxyFromEnvironment();
        if (!string.IsNullOrWhiteSpace(envProxy))
        {
            _proxyBox.Text = envProxy;
            _proxyChk.Checked = true;
            Append($"检测到环境代理: {envProxy}（已填入，可取消勾选）");
        }

        SyncProxyUi();
        Append($"输出目录: {_destBox.Text}");
        Append("点「检查清单」查看待下载数量，或直接「开始下载」。");
    }

    /// <summary>未勾选时代理输入框与测试按钮置灰，背景也随之变色，避免看起来仍可编辑。</summary>
    private void SyncProxyUi()
    {
        bool on = _proxyChk.Checked;
        _proxyBox.Enabled = on;
        _proxyTestBtn.Enabled = on;
        _proxyBox.BackColor = on ? SystemColors.Window : SystemColors.Control;
    }

    /// <summary>把界面上的代理设置应用到全局网络配置。返回 false 表示地址无效。</summary>
    private bool ApplyProxy()
    {
        try
        {
            UmaFs.Core.Net.SetProxy(_proxyChk.Checked ? _proxyBox.Text : "off");
            return true;
        }
        catch (ArgumentException ex)
        {
            Append($"代理设置错误: {ex.Message}");
            _status.Text = "代理地址无效";
            return false;
        }
    }

    /// <summary>测试代理是否可用：请求 root API 并报耗时。</summary>
    private async Task TestProxyAsync()
    {
        if (!ApplyProxy()) return;

        _proxyTestBtn.Enabled = false;
        _status.Text = "测试代理…";
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (okRoot, msgRoot, msRoot) = await Task.Run(async () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var http = UmaFs.Core.Net.CreateClient(2, TimeSpan.FromSeconds(30));
                    string body = await http.GetStringAsync(UmaFs.Core.RemoteManifest.RootApi);
                    return (true, body.Length > 80 ? body[..80] + "…" : body, t.ElapsedMilliseconds);
                }
                catch (Exception ex) { return (false, ex.Message, t.ElapsedMilliseconds); }
            });

            // root API 通不代表 CDN 通：代理出口常被 Akamai 拦成 403
            var (okCdn, msgCdn, msCdn) = await Task.Run(async () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var http = UmaFs.Core.Net.CreateClient(2, TimeSpan.FromSeconds(30));
                    string probe = "https://prd-storage-app-umamusume.akamaized.net/dl/resources/Manifest/XG/XGAGOCS7ZM7H3EWCUURVRFCTF6EE2UGT";
                    using var resp = await http.GetAsync(probe);
                    return ((int)resp.StatusCode is 200 or 206, $"HTTP {(int)resp.StatusCode}", t.ElapsedMilliseconds);
                }
                catch (Exception ex) { return (false, ex.Message, t.ElapsedMilliseconds); }
            });
            sw.Stop();

            string pl = UmaFs.Core.Net.ProxyLabel ?? "直连";
            Append($"代理: {pl}");
            Append($"  root API : {(okRoot ? "通" : "不通")}  {msRoot} ms  {msgRoot}");
            Append($"  CDN      : {(okCdn ? "通" : "不通")}  {msCdn} ms  {msgCdn}");
            if (okRoot && !okCdn)
                Append("  注意：CDN 不通（常见为 403，代理出口被 Akamai 拦）。下载会失败，需换代理或取消勾选直连。");

            _status.Text = okRoot && okCdn ? $"代理正常（{sw.ElapsedMilliseconds} ms）"
                         : okRoot ? "API 通，CDN 不通"
                         : "代理不可用";
        }
        catch (Exception ex)
        {
            Append($"代理测试失败: {ex.GetType().Name}: {ex.Message}");
            _status.Text = "代理不可用";
        }
        finally
        {
            _proxyTestBtn.Enabled = _proxyChk.Checked;
        }
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _destBox.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) _destBox.Text = dlg.SelectedPath;
    }

    private OutputLayout SelectedLayout =>
        _layoutBox.SelectedIndex == 1 ? OutputLayout.Flat : OutputLayout.Cdn;

    private GamePlatform SelectedPlatform => _platBox.SelectedIndex switch
    {
        1 => GamePlatform.Android,
        2 => GamePlatform.IOS,
        _ => GamePlatform.Windows,
    };

    private string? SelectedGroup =>
        _groupBox.SelectedIndex >= 0 && _groupBox.SelectedIndex < Groups.Length
            ? Groups[_groupBox.SelectedIndex].Value
            : "DeleteOnLogin";

    private async Task CheckAsync()
    {
        if (!ApplyProxy()) return;

        _checkBtn.Enabled = false;
        _status.Text = "读取清单…";
        _cts = new CancellationTokenSource();
        try
        {
            var ct = _cts.Token;
            string? rv = string.IsNullOrWhiteSpace(_rvBox.Text) ? null : _rvBox.Text.Trim();
            string? derived = null;
            if (rv is not null)
            {
                derived = RemoteManifest.DeriveRootHash(rv);
                Append($"由 resource_version 推导根清单: {derived}");
            }
            _manifest = await Task.Run(() => RemoteManifest.FetchAsync(SelectedPlatform, Append, ct, derived, rv), ct);
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
        if (!ApplyProxy()) return;

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
            await Task.Run(() => Cli.Run(dest, group, parallel, 0, false, false, Append, p => progress.Report(p), ct,
                SelectedPlatform, SelectedLayout, _fullChk.Checked, _pruneChk.Checked,
                string.IsNullOrWhiteSpace(_rvBox.Text) ? null : _rvBox.Text.Trim()), ct);
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
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

/// <summary>
/// 「连接手机」内置工具：开启局域网服务，手机端扫码 / 配对码 / IP 连接后可监控、截图、
/// 执行 PowerShell、winget 装软件、互传消息文件。主页面只保留服务与配对信息，
/// 聊天与文件、手机任务都收进独立弹窗；接收位置可手动选择。
/// </summary>
public sealed class PhoneLinkTool : IBuiltinTool
{
    public string Id => "phone-link";
    public string Name => LocalizationService.L("Builtin_phone-link_Name", "连接手机");
    public string Description => LocalizationService.L("Builtin_phone-link_Desc", "手机端通过局域网（二维码 / 配对码 / IP）连接电脑，查看电脑配置与屏幕截图、自选项实时监控（含 FPS 曲线），远程执行 PowerShell、winget 安装软件，并与电脑互传文字、图片和文件。");
    public string Glyph => "\uE8EA";
    public string Category => "网络工具";
    public BuiltinToolKind Kind => BuiltinToolKind.BackgroundTask;

    public Task ExecuteAsync(BuiltinToolContext context)
    {
        App.MainWindow?.NavigateToToolPage(typeof(ToolContentPage), new ToolContentPageParam
        {
            Title = Name,
            Description = Description,
            Glyph = Glyph,
            Content = BuildContent()
        });
        return Task.CompletedTask;
    }

    private static UIElement BuildContent()
    {
        var toggle = new ToggleSwitch { Header = "启用手机连接服务", OnContent = "已开启", OffContent = "已关闭", IsOn = PhoneLinkService.IsRunning };
        var warn = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = "仅在可信的局域网使用。配对后的手机拥有执行 PowerShell 命令的权限，不用时请关闭服务。首次开启时 Windows 防火墙可能需要放行。"
        };
        var ipBox = new ComboBox { Header = "电脑 IP 地址", MinWidth = 220 };
        var qr = new Image { Width = 200, Height = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var codeText = new TextBlock { FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true };
        var addrText = new TextBlock { IsTextSelectionEnabled = true, Opacity = 0.8 };
        var devices = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "刷新配对码" };
        var revoke = new Button { Content = "断开所有手机" };

        var chatButton = new Button { Content = "聊天与文件…" };
        var jobsButton = new Button { Content = "手机任务…" };
        var receivePathText = new TextBlock
        {
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var changeReceive = new Button { Content = "更改…" };
        var resetReceive = new Button { Content = "恢复默认", Visibility = Visibility.Collapsed };
        var openInbox = new Button { Content = "打开文件夹" };

        var receiveRow = new Grid { ColumnSpacing = 8 };
        receiveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        receiveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        receiveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        receiveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(changeReceive, 1);
        Grid.SetColumn(resetReceive, 2);
        Grid.SetColumn(openInbox, 3);
        receiveRow.Children.Add(receivePathText);
        receiveRow.Children.Add(changeReceive);
        receiveRow.Children.Add(resetReceive);
        receiveRow.Children.Add(openInbox);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24, 8, 24, 24) };
        panel.Children.Add(toggle);
        panel.Children.Add(warn);
        panel.Children.Add(ipBox);
        panel.Children.Add(qr);
        panel.Children.Add(new TextBlock { Text = "配对码", Opacity = 0.7 });
        panel.Children.Add(codeText);
        panel.Children.Add(addrText);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, revoke } });
        panel.Children.Add(devices);
        panel.Children.Add(new TextBlock { Text = "手机互动", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chatButton, jobsButton } });
        panel.Children.Add(new TextBlock { Text = "文件接收", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(receiveRow);

        var root = new ScrollViewer { Content = panel };

        void Refresh()
        {
            toggle.IsOn = PhoneLinkService.IsRunning;
            var running = PhoneLinkService.IsRunning;
            ipBox.IsEnabled = running;
            refresh.IsEnabled = revoke.IsEnabled = running;
            qr.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running)
            {
                codeText.Text = "—";
                addrText.Text = "";
                devices.Text = "";
                return;
            }
            var ip = ipBox.SelectedItem as string ?? PhoneLinkService.GetLocalIps()[0];
            codeText.Text = PhoneLinkService.PairCode;
            addrText.Text = $"手动连接：{ip}:{PhoneLinkService.Port}";
            var names = PhoneLinkService.PairedDeviceNames;
            devices.Text = names.Count == 0 ? "暂无已连接的手机" : "已连接：" + string.Join("、", names);
            _ = UpdateQrAsync(qr, PhoneLinkService.BuildQrPayload(ip));
        }

        void RefreshReceive()
        {
            var path = PhoneLinkService.ReceiveDir;
            receivePathText.Text = "接收位置：" + path;
            ToolTipService.SetToolTip(receivePathText, path);
            resetReceive.Visibility = PhoneLinkService.CustomReceiveDir is null ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshJobsButton()
        {
            var runningJobs = PhoneLinkService.GetJobs().Count(j => j.IsRunning);
            jobsButton.Content = runningJobs > 0 ? $"手机任务…（{runningJobs} 个进行中）" : "手机任务…";
        }

        void FillIps()
        {
            ipBox.ItemsSource = PhoneLinkService.GetLocalIps();
            ipBox.SelectedIndex = 0;
        }
        FillIps();

        void ShowChatDialog()
        {
            if (root.XamlRoot is null) return;
            var dialog = new PhoneLinkChatDialog(root.XamlRoot);
            _ = ContentDialogGuard.ShowWhenIdleAsync(dialog, TimeSpan.FromSeconds(10));
        }

        void ShowJobsDialog(string? jobId = null)
        {
            if (root.XamlRoot is null) return;
            var dialog = new PhoneLinkJobsDialog(root.XamlRoot, jobId);
            _ = ContentDialogGuard.ShowWhenIdleAsync(dialog, TimeSpan.FromSeconds(10));
        }

        async Task RunLoadedTasksAsync()
        {
            try
            {
                await MaybePromptCloseToTrayAsync();
                var pending = PhoneLinkActivation.TryConsume();
                if (pending is null) return;
                if (pending.Target == "chat") ShowChatDialog();
                else if (pending.Target == "jobs") ShowJobsDialog(pending.JobId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhoneLink] 首屏任务失败：{ex.Message}");
            }
        }

        Action stateHandler = () => root.DispatcherQueue.TryEnqueue(Refresh);
        Action jobsHandler = () => root.DispatcherQueue.TryEnqueue(RefreshJobsButton);
        root.Loaded += (_, _) =>
        {
            PhoneLinkService.StateChanged += stateHandler;
            PhoneLinkService.JobsChanged += jobsHandler;
            Refresh();
            RefreshReceive();
            RefreshJobsButton();
            _ = RunLoadedTasksAsync();
        };
        root.Unloaded += (_, _) =>
        {
            PhoneLinkService.StateChanged -= stateHandler;
            PhoneLinkService.JobsChanged -= jobsHandler;
        };

        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn == PhoneLinkService.IsRunning) return;
            try
            {
                if (toggle.IsOn) PhoneLinkService.Start(); else PhoneLinkService.Stop();
            }
            catch (Exception ex)
            {
                addrText.Text = "启动失败：" + ex.Message;
            }
            Refresh();
            RefreshJobsButton();
        };
        ipBox.SelectionChanged += (_, _) => { if (PhoneLinkService.IsRunning) Refresh(); };
        refresh.Click += (_, _) => PhoneLinkService.RefreshPairCode();
        revoke.Click += (_, _) => PhoneLinkService.RevokeAll();
        chatButton.Click += (_, _) => ShowChatDialog();
        jobsButton.Click += (_, _) => ShowJobsDialog();
        changeReceive.Click += async (_, _) =>
        {
            try
            {
                var folder = await Win32Dialogs.PickFolderAsync();
                if (string.IsNullOrWhiteSpace(folder)) return;
                PhoneLinkService.SetReceiveDir(folder);
                RefreshReceive();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhoneLink] 选择接收位置失败：{ex.Message}");
            }
        };
        resetReceive.Click += (_, _) =>
        {
            PhoneLinkService.SetReceiveDir(null);
            RefreshReceive();
        };
        openInbox.Click += (_, _) =>
        {
            try
            {
                var dir = PhoneLinkService.ReceiveDir;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        };

        Refresh();
        RefreshReceive();
        RefreshJobsButton();
        return root;
    }

    /// <summary>
    /// 首次打开本工具时询问是否允许「关闭窗口后留在系统托盘」——
    /// 手机连接是后台服务，关窗即退出会让手机再也连不上；文案照搬设置项。
    /// </summary>
    private static async Task MaybePromptCloseToTrayAsync()
    {
        if (AppSettings.Get("PhoneLinkCloseToTrayPrompted") is not null) return;
        if (CloseToTrayService.IsEnabled)
        {
            // 已经是开启状态：无需再问，直接记标记
            AppSettings.Set("PhoneLinkCloseToTrayPrompted", "1");
            return;
        }
        var xamlRoot = App.MainWindow?.Content?.XamlRoot;
        if (xamlRoot is null) return; // 窗口还没就绪：不消耗标记，下次打开再问

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ThemeService.CurrentElementTheme,
            Title = LocalizationService.L("Settings_CloseToTray_Title", "关闭主窗口时最小化到系统托盘"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = LocalizationService.L("Settings_CloseToTray_Desc",
                           "点关闭按钮不再退出程序，而是留在系统托盘继续运行，避免误关闭后重启时重新检测硬件信息；双击托盘图标恢复窗口，右键可退出")
                       + "\n\n开启后关闭窗口时，手机连接会继续在系统托盘后台运行，手机随时可以再连上。"
            },
            PrimaryButtonText = "开启",
            CloseButtonText = "暂不开启",
            DefaultButton = ContentDialogButton.Primary
        };
        var confirmed = false;
        dialog.PrimaryButtonClick += (_, _) => confirmed = true;
        // 弹窗真的出现（无论选哪个按钮）才算「问过了」；被其他弹窗挡住没弹出来则下次再问
        var shown = await ContentDialogGuard.ShowWhenIdleAsync(dialog, TimeSpan.FromSeconds(10));
        if (!shown) return;
        AppSettings.Set("PhoneLinkCloseToTrayPrompted", "1");
        if (confirmed) CloseToTrayService.SetEnabled(true);
    }

    private static async Task UpdateQrAsync(Image target, string payload)
    {
        try
        {
            var png = QrCodeGenerator.GeneratePng(payload, 400);
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(png));
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            target.Source = bmp;
        }
        catch { }
    }
}

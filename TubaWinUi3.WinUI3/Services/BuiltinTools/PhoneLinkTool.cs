using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TubaWinUi3.Pages;

namespace TubaWinUi3.Services;

/// <summary>「连接手机」内置工具：开启局域网服务，手机端扫码 / 配对码 / IP 连接后可监控、截图、执行 PowerShell、winget 装软件、互传消息文件。</summary>
public sealed class PhoneLinkTool : IBuiltinTool
{
    public string Id => "phone-link";
    public string Name => LocalizationService.L("Builtin_phone-link_Name", "连接手机");
    public string Description => LocalizationService.L("Builtin_phone-link_Desc", "手机端通过局域网（二维码 / 配对码 / IP）连接电脑，实时查看硬件监控与屏幕截图，远程执行 PowerShell、winget 安装软件，并与电脑互传文字、图片和文件。");
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

        var chatList = new ListView { Height = 220, SelectionMode = ListViewSelectionMode.None };
        var input = new TextBox { PlaceholderText = "发送给手机的文字…", HorizontalAlignment = HorizontalAlignment.Stretch };
        var send = new Button { Content = "发送" };
        var sendFile = new Button { Content = "发送文件…" };
        var openInbox = new Button { Content = "打开接收文件夹" };

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
        panel.Children.Add(new TextBlock { Text = "聊天 / 文件传输", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(chatList);
        var sendRow = new Grid { ColumnSpacing = 8 };
        sendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sendRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(send, 1);
        Grid.SetColumn(sendFile, 2);
        sendRow.Children.Add(input);
        sendRow.Children.Add(send);
        sendRow.Children.Add(sendFile);
        panel.Children.Add(sendRow);
        panel.Children.Add(openInbox);

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

        void FillIps()
        {
            ipBox.ItemsSource = PhoneLinkService.GetLocalIps();
            ipBox.SelectedIndex = 0;
        }
        FillIps();

        void AppendMessage(PhoneChatMessage m)
        {
            var who = m.From == "phone" ? "手机" : "电脑";
            var body = m.Type == "text" ? m.Text : $"[{(m.Type == "image" ? "图片" : "文件")}] {m.FileName} ({m.Size / 1024.0:F1} KB)";
            chatList.Items.Add($"{DateTimeOffset.FromUnixTimeMilliseconds(m.Time).LocalDateTime:HH:mm:ss}  {who}：{body}");
            chatList.ScrollIntoView(chatList.Items[^1]);
        }
        foreach (var m in PhoneLinkService.GetMessages()) AppendMessage(m);

        Action stateHandler = () => root.DispatcherQueue.TryEnqueue(Refresh);
        Action<PhoneChatMessage> msgHandler = m => root.DispatcherQueue.TryEnqueue(() => AppendMessage(m));
        root.Loaded += (_, _) =>
        {
            PhoneLinkService.StateChanged += stateHandler;
            PhoneLinkService.MessageAdded += msgHandler;
            Refresh();
        };
        root.Unloaded += (_, _) =>
        {
            PhoneLinkService.StateChanged -= stateHandler;
            PhoneLinkService.MessageAdded -= msgHandler;
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
        };
        ipBox.SelectionChanged += (_, _) => { if (PhoneLinkService.IsRunning) Refresh(); };
        refresh.Click += (_, _) => PhoneLinkService.RefreshPairCode();
        revoke.Click += (_, _) => PhoneLinkService.RevokeAll();
        void DoSend()
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            PhoneLinkService.AddPcText(input.Text.Trim());
            input.Text = "";
        }
        send.Click += (_, _) => DoSend();
        input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) DoSend(); };
        sendFile.Click += (_, _) =>
        {
            foreach (var f in Win32Dialogs.PickOpenMultiple("所有文件\0*.*\0\0", "选择要发送给手机的文件"))
                PhoneLinkService.AddPcFile(f);
        };
        openInbox.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(PhoneLinkService.InboxDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PhoneLinkService.InboxDir) { UseShellExecute = true });
            }
            catch { }
        };

        Refresh();
        return root;
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

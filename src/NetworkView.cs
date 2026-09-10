using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThermalDot;

public sealed partial class DotWindow
{
    private Grid networkView = null!;
    private Button networkDot = null!;
    private readonly NetworkSampler networkSampler = new();
    private NetworkReading networkReading = new();
    private ProxyReading proxyReading = new();
    private DispatcherTimer? networkTimer;
    private CancellationTokenSource? proxyRequest;
    private Task? controlOperation;
    private bool networkBusy, proxyBusy, controlBusy, controlReadingBusy;
    private ProxyControlState controlState = new();
    private DateTime nextControlCheck = DateTime.MinValue, controlMessageUntil;
    private string controlMessage = "";
    private int proxyGeneration;
    private DateTime nextProxyCheck = DateTime.MinValue;


    private Button NetButton(string name) => (Button)networkView.FindName(name);

    private void NetText(string name, string text)
    {
        var field = (TextBlock)networkView.FindName(name); field.Text = text; field.ToolTip = text;
    }
    private void InitializeNetworkView(Grid pages)
    {
        networkView = (Grid)XamlReader.Parse(NetworkXaml);
        networkView.Resources = screenView.Resources;
        networkView.Visibility = Visibility.Collapsed; pages.Children.Add(networkView);
        networkDot = (Button)face.FindName("NetworkDot");
        networkDot.Click += (_, _) => { TogglePanel(true); ShowNetworkView(); };
        NetButton("BackNetwork").Click += (_, _) => ShowTemperatureView();
        NetButton("CloseNetwork").Click += (_, _) => panel.Hide();
        NetButton("PauseNetwork").Click += (_, _) =>
        {
            prefs.NetworkPaused = !prefs.NetworkPaused; Save(); proxyGeneration++; proxyRequest?.Cancel();
            proxyReading.DelayMs = null; nextProxyCheck = DateTime.MinValue; PaintNetwork();
        };
        NetButton("RefreshNetwork").Click += async (_, _) => await RefreshProxy();
        NetButton("ModeGlobal").Click += async (_, _) => await ChangeProxy("mode", "global");
        NetButton("ModeRule").Click += async (_, _) => await ChangeProxy("mode", "rule");
        NetButton("ModeOff").Click += async (_, _) => await ChangeProxy("mode", "off");
        NetButton("UsNode1").Click += async (_, _) => { if (controlState.Nodes.Count > 0) await ChangeProxy("node", controlState.Nodes[0]); };
        NetButton("UsNode2").Click += async (_, _) => { if (controlState.Nodes.Count > 1) await ChangeProxy("node", controlState.Nodes[1]); };
        PaintNetwork();
        if (isPreview) return;
        networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        networkTimer.Tick += async (_, _) => await UpdateNetwork();
        networkTimer.Start();
    }
    private void ShowTemperatureView()
    {
        powerView.Visibility = Visibility.Collapsed; screenView.Visibility = Visibility.Collapsed;
        networkView.Visibility = Visibility.Collapsed; detail.Visibility = Visibility.Visible;
    }
    private void ShowNetworkView()
    {
        detail.Visibility = Visibility.Collapsed; powerView.Visibility = Visibility.Collapsed; screenView.Visibility = Visibility.Collapsed;
        networkView.Visibility = Visibility.Visible; PaintNetwork();
        if (!isPreview) _ = RefreshControl();
    }
    private async Task UpdateNetwork()
    {
        if (networkBusy || closing) return;
        networkBusy = true;
        try
        {
            networkReading = await Task.Run(networkSampler.Read);
            if (closing) return;
            PaintNetwork();
            if (!controlBusy && !controlReadingBusy && DateTime.UtcNow >= nextControlCheck) _ = RefreshControl();
            if (!controlBusy && controlState.ActiveMode is "global" or "rule" && !prefs.NetworkPaused && !proxyBusy && DateTime.UtcNow >= nextProxyCheck) _ = RefreshProxy();
            if (DateTime.Now.Second % 5 == 0)
                try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "network.json"), JsonSerializer.Serialize(new { Network = networkReading, Proxy = proxyReading, Control = controlState, Paused = prefs.NetworkPaused }, Program.Json)); } catch { }
        }
        finally { networkBusy = false; }
    }
    private async Task RefreshProxy()
    {
        if (proxyBusy || controlBusy || controlState.ActiveMode is not ("global" or "rule") || prefs.NetworkPaused || closing || isPreview) return;
        proxyBusy = true; int generation = proxyGeneration;
        nextProxyCheck = DateTime.UtcNow.AddSeconds(15);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        proxyRequest = request; PaintNetwork();
        try
        {
            var reading = await ClashMonitor.Read("", request.Token);
            if (generation == proxyGeneration && !closing) proxyReading = reading;
        }
        catch (OperationCanceledException) { }
        finally
        {
            proxyRequest = null; proxyBusy = false;
            if (!closing) PaintNetwork();
        }
    }
    private void PaintNetwork()
    {
        NetText("NetworkSupply", networkReading.Connected == true ? networkReading.Adapter + " · 接口已连接"
            : networkReading.Connected == false ? "网络接口已断开" : "正在检测主网络接口");
        NetText("DownloadSpeed", NetworkSampler.Speed(networkReading.Download));
        NetText("UploadSpeed", NetworkSampler.Speed(networkReading.Upload));
        NetText("ProxyMode", controlState.ActiveMode switch { "rule" => "规则已开启", "global" => "全局已开启", "off" => "已关闭", _ => "状态待确认" });
        NetText("ProxyNode", controlState.SelectedNode != "" ? controlState.SelectedNode : controlState.Selections.Count > 0 ? "全局 / 规则节点不同，可点击统一切换" : "美国节点暂不可用");
        NetText("DelayValue", controlState.ActiveMode == "off" ? "已关闭" : prefs.NetworkPaused ? "已暂停" : proxyReading.DelayMs.HasValue ? proxyReading.DelayMs + " ms" : "—");
        NetText("NetworkStatus", controlBusy ? "正在切换并确认…" : DateTime.UtcNow < controlMessageUntil ? controlMessage : controlState.Error != "" ? controlState.Error : controlState.ActiveMode == "off" ? "系统代理已关闭 · Clash 保持后台运行" : prefs.NetworkPaused ? "延时检查已暂停；网速继续更新。" : proxyBusy ? "正在读取节点并检查连接延时…" :
            proxyReading.Error != "" ? proxyReading.Error : proxyReading.DelayMs.HasValue ? "最近检查 " + proxyReading.Time.ToString("HH:mm:ss") + " · 当前节点 → 测试站点" : "正在等待首次延时检查");
        NetText("NetworkDetail", networkReading.Error == "" ? "统计一个主网络接口 · 1 MB = 1,000 KB" : networkReading.Error);
        NetButton("PauseNetwork").Content = prefs.NetworkPaused ? "恢复延时检查" : "暂停延时检查";
        NetButton("RefreshNetwork").IsEnabled = !prefs.NetworkPaused && !proxyBusy && !controlBusy && controlState.ActiveMode is "rule" or "global";
        Color color = networkReading.Connected == false ? Color.FromRgb(255, 112, 112) :
            !prefs.NetworkPaused && proxyReading.DelayMs >= 300 ? Color.FromRgb(255, 198, 105) :
            networkReading.Connected == true ? Blue : Color.FromRgb(116, 134, 151);
        networkDot.Background = new SolidColorBrush(color);
        networkDot.ToolTip = "↓ " + NetworkSampler.Speed(networkReading.Download) + "    ↑ " + NetworkSampler.Speed(networkReading.Upload) +
            "\n" + (prefs.NetworkPaused ? "延时检查已暂停" : proxyReading.DelayMs.HasValue ? "节点延时 " + proxyReading.DelayMs + " ms" : "节点延时暂不可用") +
            "\n单击打开网络信息";
        PaintControlButton("ModeGlobal", controlState.ActiveMode == "global", controlState.Ready);
        PaintControlButton("ModeRule", controlState.ActiveMode == "rule", controlState.Ready);
        PaintControlButton("ModeOff", controlState.ActiveMode == "off", controlState.Ready);
        for (int i = 0; i < 2; i++)
        {
            string name = i == 0 ? "UsNode1" : "UsNode2";
            string node = controlState.Nodes.Count > i ? controlState.Nodes[i] : "";
            NetButton(name).Content = node == "" ? "美国节点 " + (i + 1) : ProxyControl.NodeLabel(node, i);
            NetButton(name).ToolTip = node == "" ? "未检测到兼容节点" : node;
            PaintControlButton(name, node != "" && controlState.SelectedNode == node, controlState.Ready && controlState.Nodes.Count == 2 && controlState.Selections.Count >= 2);
        }
    }
    private void PaintControlButton(string name, bool selected, bool enabled)
    {
        var button = NetButton(name);
        button.IsEnabled = enabled && !controlBusy;
        button.Background = new SolidColorBrush(selected ? Color.FromRgb(43, 76, 103) : Color.FromRgb(42, 57, 73));
        button.BorderBrush = new SolidColorBrush(selected ? Blue : Color.FromRgb(67, 83, 101));
        button.Foreground = selected ? new SolidColorBrush(Blue) : new SolidColorBrush(Color.FromRgb(198, 214, 227));
    }
    private async Task RefreshControl()
    {
        if (controlBusy || controlReadingBusy || closing || isPreview) return;
        controlReadingBusy = true; int generation = proxyGeneration; nextControlCheck = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var value = await ProxyControl.Read(cancel.Token);
            if (!closing && generation == proxyGeneration)
            {
                if (value.ActiveMode != controlState.ActiveMode || value.SelectedNode != controlState.SelectedNode)
                { proxyGeneration++; proxyRequest?.Cancel(); proxyReading.DelayMs = null; nextProxyCheck = DateTime.MinValue; }
                controlState = value; PaintNetwork();
            }
        }
        catch (OperationCanceledException) { }
        finally { controlReadingBusy = false; }
    }
    private async Task ChangeProxy(string action, string value)
    {
        if (controlBusy || closing || isPreview || !controlState.Ready) return;
        controlBusy = true; proxyGeneration++; proxyRequest?.Cancel(); proxyReading.DelayMs = null; PaintNetwork();
        try
        {
            var operation = ProxyControl.Change(controlState, action, value, cancel.Token);
            controlOperation = operation; var result = await operation;
            if (!closing) { controlState = result.State; controlMessage = result.Message; controlMessageUntil = DateTime.UtcNow.AddSeconds(10); }
        }
        finally { controlBusy = false; nextControlCheck = nextProxyCheck = DateTime.MinValue; if (!closing) PaintNetwork(); }
    }
    private void RestoreDot(bool resetPosition = false)
    {
        if (closing) return;
        WindowState = WindowState.Normal;
        if (resetPosition)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 22; Top = area.Top + area.Height * .42;
        }
        Show(); ClampToScreen(); Topmost = false; Topmost = true; Activate(); Save();
    }
    public void RenderNetworkPreview(NetworkReading network, ProxyReading proxy, bool paused, string output, ProxyControlState? control = null)
    {
        controlState = control ?? new ProxyControlState(); networkReading = network; proxyReading = proxy; prefs.NetworkPaused = paused; prefs.NetworkGroup = "";
        ShowNetworkView();
        face.Measure(new Size(100, 100)); face.Arrange(new Rect(0, 0, 100, 100)); face.UpdateLayout();
        networkView.Measure(new Size(376, 490)); networkView.Arrange(new Rect(0, 0, 376, 490)); networkView.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 18, 29)), null, new Rect(0, 0, 536, 522));
            dc.DrawRectangle(new VisualBrush(networkView), null, new Rect(12, 16, 376, 490));
            dc.DrawRectangle(new VisualBrush(face), null, new Rect(405, 205, 116, 116));
        }
        var bitmap = new RenderTargetBitmap(1072, 1044, 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output); encoder.Save(stream);
    }
    private const string NetworkXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent">
 <Border Margin="9" CornerRadius="22" Background="#F41B2736" BorderBrush="#425263" BorderThickness="0.7">
  <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="3" Opacity="0.3"/></Border.Effect>
  <Grid Margin="18,15">
   <Grid.RowDefinitions><RowDefinition Height="34"/><RowDefinition Height="27"/><RowDefinition Height="84"/><RowDefinition Height="25"/><RowDefinition Height="188"/><RowDefinition Height="40"/><RowDefinition Height="*"/></Grid.RowDefinitions>
   <Button x:Name="BackNetwork" Content="‹" FontSize="22" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="26" HorizontalAlignment="Left" VerticalAlignment="Top" ToolTip="返回温度与资源"/>
   <TextBlock Text="网络与节点" Foreground="#ECF4F9" FontSize="17" FontWeight="SemiBold" Margin="32,0,0,0"/>
   <Button x:Name="CloseNetwork" Content="×" FontSize="19" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="25" HorizontalAlignment="Right" VerticalAlignment="Top"/>
   <TextBlock Grid.Row="1" x:Name="NetworkSupply" Foreground="#8FAEC4" FontSize="11" TextTrimming="CharacterEllipsis"/>
   <Grid Grid.Row="2">
    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
    <Border Background="#233A47" CornerRadius="13" Padding="12,11"><StackPanel><TextBlock Text="↓  下载" Foreground="#93C8FA" FontSize="11"/><TextBlock x:Name="DownloadSpeed" Text="—" Foreground="#EBF5FE" FontFamily="Segoe UI" FontWeight="SemiBold" FontSize="21" Margin="0,7,0,0"/></StackPanel></Border>
    <Border Grid.Column="2" Background="#233D3E" CornerRadius="13" Padding="12,11"><StackPanel><TextBlock Text="↑  上传" Foreground="#83DBC5" FontSize="11"/><TextBlock x:Name="UploadSpeed" Text="—" Foreground="#E5FBF4" FontFamily="Segoe UI" FontWeight="SemiBold" FontSize="21" Margin="0,7,0,0"/></StackPanel></Border>
   </Grid>
   <TextBlock Grid.Row="3" x:Name="NetworkDetail" Foreground="#68899F" FontSize="9" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
   <Border Grid.Row="4" Background="#223344" CornerRadius="13" Padding="12,11">
    <Grid>
     <Grid.RowDefinitions><RowDefinition Height="22"/><RowDefinition Height="32"/><RowDefinition Height="9"/><RowDefinition Height="32"/><RowDefinition Height="25"/><RowDefinition Height="*"/></Grid.RowDefinitions>
     <TextBlock Text="Clash · 代理控制" Foreground="#D7E8F5" FontSize="12" FontWeight="SemiBold"/>
     <TextBlock x:Name="ProxyMode" Foreground="#84A9C7" FontSize="10" HorizontalAlignment="Right"/>
     <UniformGrid Grid.Row="1" Columns="3" Margin="-3,0"><Button x:Name="ModeGlobal" Content="全局" Margin="3,0" Padding="4,6"/><Button x:Name="ModeRule" Content="规则" Margin="3,0" Padding="4,6"/><Button x:Name="ModeOff" Content="关闭" Margin="3,0" Padding="4,6" ToolTip="关闭系统代理并让 Clash 使用直连模式，保留后台进程"/></UniformGrid>
     <UniformGrid Grid.Row="3" Columns="2" Margin="-3,0"><Button x:Name="UsNode1" Content="美国Y01" Margin="3,0" Padding="4,6"/><Button x:Name="UsNode2" Content="美国Y02" Margin="3,0" Padding="4,6"/></UniformGrid>
     <TextBlock Grid.Row="4" x:Name="ProxyNode" Foreground="#E4EFF8" FontSize="10" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
     <TextBlock Grid.Row="5" x:Name="DelayValue" Foreground="#A6CFFF" FontFamily="Segoe UI, Microsoft YaHei UI" FontSize="28" FontWeight="SemiBold" VerticalAlignment="Bottom"/>
     <TextBlock Grid.Row="5" Text="节点连接延时" Foreground="#88A9C0" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,0,6"/>
    </Grid>
   </Border>
   <TextBlock Grid.Row="5" x:Name="NetworkStatus" Foreground="#A7C2D4" FontSize="10" TextWrapping="Wrap" Margin="0,8,0,2" LineHeight="14"/>
   <StackPanel Grid.Row="6" VerticalAlignment="Bottom">
    <Grid><Button x:Name="PauseNetwork" Content="暂停延时检查" Padding="8,5" FontSize="10" HorizontalAlignment="Left"/><Button x:Name="RefreshNetwork" Content="立即检查" Padding="8,5" FontSize="10" HorizontalAlignment="Right"/></Grid>
    <TextBlock Text="网速 1 秒更新 · 延时 15 秒检查 · gstatic" Foreground="#68899F" FontSize="9" Margin="0,8,0,0" ToolTip="通过所示节点请求 https://www.gstatic.com/generate_204，表示该节点到测试站点的请求用时。规则模式下其他网站可能走其他节点。"/>
   </StackPanel>
  </Grid>
 </Border>
</Grid>
""";
}
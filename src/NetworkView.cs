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
    private bool networkBusy, proxyBusy, loadingGroups;
    private int proxyGeneration;
    private DateTime nextProxyCheck = DateTime.MinValue;
    private string groupListSignature = "";
    private const string AutoGroup = "自动（按当前模式）";
    private Button NetButton(string name) => (Button)networkView.FindName(name);
    private ComboBox GroupPicker => (ComboBox)networkView.FindName("ProxyGroup");
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
        GroupPicker.SelectionChanged += (_, _) =>
        {
            if (loadingGroups || GroupPicker.SelectedItem is not string value) return;
            prefs.NetworkGroup = value == AutoGroup ? "" : value; Save();
            proxyGeneration++; proxyRequest?.Cancel(); proxyReading.DelayMs = null;
            proxyReading.Node = ""; proxyReading.Group = ""; proxyReading.Error = "";
            nextProxyCheck = DateTime.MinValue; PaintNetwork();
        };
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
            if (!prefs.NetworkPaused && !proxyBusy && DateTime.UtcNow >= nextProxyCheck) _ = RefreshProxy();
            if (DateTime.Now.Second % 5 == 0)
                try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "network.json"), JsonSerializer.Serialize(new { Network = networkReading, Proxy = proxyReading, Paused = prefs.NetworkPaused }, Program.Json)); } catch { }
        }
        finally { networkBusy = false; }
    }
    private async Task RefreshProxy()
    {
        if (proxyBusy || prefs.NetworkPaused || closing || isPreview) return;
        proxyBusy = true; int generation = proxyGeneration;
        nextProxyCheck = DateTime.UtcNow.AddSeconds(15);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        proxyRequest = request; PaintNetwork();
        try
        {
            var reading = await ClashMonitor.Read(prefs.NetworkGroup, request.Token);
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
        NetText("ProxyMode", proxyReading.Mode switch { "rule" => "规则模式", "global" => "全局模式", "direct" => "直连模式", _ => "等待连接" });
        NetText("ProxyNode", proxyReading.Node == "" ? "节点暂不可用" : proxyReading.Node);
        NetText("ObservedGroup", proxyReading.Group == "" ? "选择策略组以查看它的当前节点" : "正在监测：" + proxyReading.Group);
        NetText("DelayValue", prefs.NetworkPaused ? "已暂停" : proxyReading.DelayMs.HasValue ? proxyReading.DelayMs + " ms" : "—");
        NetText("NetworkStatus", prefs.NetworkPaused ? "延时检查已暂停；网速继续更新。" : proxyBusy ? "正在读取节点并检查连接延时…" :
            proxyReading.Error != "" ? proxyReading.Error : proxyReading.DelayMs.HasValue ? "最近检查 " + proxyReading.Time.ToString("HH:mm:ss") + " · 当前节点 → 测试站点" : "正在等待首次延时检查");
        NetText("NetworkDetail", networkReading.Error == "" ? "统计一个主网络接口 · 1 MB = 1,000 KB" : networkReading.Error);
        NetButton("PauseNetwork").Content = prefs.NetworkPaused ? "恢复延时检查" : "暂停延时检查";
        NetButton("RefreshNetwork").IsEnabled = !prefs.NetworkPaused && !proxyBusy;
        Color color = networkReading.Connected == false ? Color.FromRgb(255, 112, 112) :
            !prefs.NetworkPaused && proxyReading.DelayMs >= 300 ? Color.FromRgb(255, 198, 105) :
            networkReading.Connected == true ? Blue : Color.FromRgb(116, 134, 151);
        networkDot.Background = new SolidColorBrush(color);
        networkDot.ToolTip = "↓ " + NetworkSampler.Speed(networkReading.Download) + "    ↑ " + NetworkSampler.Speed(networkReading.Upload) +
            "\n" + (prefs.NetworkPaused ? "延时检查已暂停" : proxyReading.DelayMs.HasValue ? "节点延时 " + proxyReading.DelayMs + " ms" : "节点延时暂不可用") +
            "\n单击打开网络信息";
        var groups = new List<string> { AutoGroup }; groups.AddRange(proxyReading.Groups);
        if (prefs.NetworkGroup != "" && !groups.Contains(prefs.NetworkGroup)) groups.Add(prefs.NetworkGroup);
        string signature = JsonSerializer.Serialize(groups);
        if (signature != groupListSignature)
        {
            loadingGroups = true; GroupPicker.ItemsSource = groups;
            GroupPicker.SelectedItem = prefs.NetworkGroup == "" ? AutoGroup : prefs.NetworkGroup;
            loadingGroups = false; groupListSignature = signature;
        }
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
    public void RenderNetworkPreview(NetworkReading network, ProxyReading proxy, bool paused, string output)
    {
        networkReading = network; proxyReading = proxy; prefs.NetworkPaused = paused; prefs.NetworkGroup = "";
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
     <Grid.RowDefinitions><RowDefinition Height="25"/><RowDefinition Height="35"/><RowDefinition Height="30"/><RowDefinition Height="20"/><RowDefinition Height="*"/></Grid.RowDefinitions>
     <TextBlock Text="Clash · VPN / 代理" Foreground="#D7E8F5" FontSize="12" FontWeight="SemiBold"/>
     <TextBlock x:Name="ProxyMode" Foreground="#84A9C7" FontSize="10" HorizontalAlignment="Right"/>
     <ComboBox Grid.Row="1" x:Name="ProxyGroup" FontSize="11" Height="34" DisplayMemberPath="" SelectedValuePath="" AutomationProperties.Name="选择要监测的 Clash 策略组" ToolTip="只改变球球监测的策略组，节点选择由 Clash 管理"/>
     <TextBlock Grid.Row="2" x:Name="ProxyNode" Foreground="#E4EFF8" FontSize="12" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
     <TextBlock Grid.Row="3" x:Name="ObservedGroup" Foreground="#779AAF" FontSize="9" TextTrimming="CharacterEllipsis"/>
     <TextBlock Grid.Row="4" x:Name="DelayValue" Foreground="#A6CFFF" FontFamily="Segoe UI, Microsoft YaHei UI" FontSize="28" FontWeight="SemiBold" VerticalAlignment="Bottom"/>
     <TextBlock Grid.Row="4" Text="节点连接延时" Foreground="#88A9C0" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,0,6"/>
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
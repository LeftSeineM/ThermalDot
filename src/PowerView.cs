using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
    private Grid powerView = null!;
    private DispatcherTimer? powerTimer;
    private PowerState powerState = new();
    private ChargeState chargeState = new();
    private bool powerBusy;

    private Button PowerButton(string name) => (Button)powerView.FindName(name);
    private void PowerText(string name, string text) => ((TextBlock)powerView.FindName(name)).Text = text;
    private void InitializePowerView()
    {
        powerView = (Grid)XamlReader.Parse(PowerXaml);
        panel.Content = null;
        var pages = new Grid(); pages.Children.Add(detail); pages.Children.Add(powerView);
        powerView.Visibility = Visibility.Collapsed; InitializeScreenView(pages); InitializeNetworkView(pages); panel.Content = pages;
        ((Button)detail.FindName("OpenPower")).Click += (_, _) => ShowPowerView();
        PowerButton("Back").Click += (_, _) => { ShowTemperatureView(); };
        PowerButton("OpenScreen").Click += (_, _) => ShowScreenView();
        PowerButton("ClosePower").Click += (_, _) => panel.Hide();
        PowerButton("Efficiency").Click += async (_, _) => await ChangePower("省电");
        PowerButton("Balanced").Click += async (_, _) => await ChangePower("平衡");
        PowerButton("Performance").Click += async (_, _) => await ChangePower("性能");
        PowerButton("Conservation").Click += async (_, _) => await ChangeCharge(1);
        PowerButton("NormalCharge").Click += async (_, _) => await ChangeCharge(0);
        PowerButton("WindowsSettings").Click += (_, _) => OpenSettings("ms-settings:powersleep");
        PowerButton("LenovoSettings").Click += (_, _) => Distribution.OpenVendorSettings();
        PaintPower();
        if (isPreview) return;
        powerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        powerTimer.Tick += async (_, _) => { if (panel.IsVisible && powerView.IsVisible) await RefreshPower(); };
        powerTimer.Start();
    }
    private async void ShowPowerView()
    {
        networkView.Visibility = Visibility.Collapsed; screenView.Visibility = Visibility.Collapsed; detail.Visibility = Visibility.Collapsed; powerView.Visibility = Visibility.Visible;
        if (!isPreview) await RefreshPower();
    }
    private async Task RefreshPower()
    {
        if (powerBusy || closing) return;
        powerBusy = true; PaintPower();
        try
        {
            powerState = PowerControl.Read(); chargeState = await PowerControl.ReadCharge();
            SavePowerSnapshot();
        }
        catch (Exception e) { chargeState = new ChargeState { Error = e.Message }; }
        finally { powerBusy = false; if (!closing) PaintPower(); }
    }
    private async Task ChangePower(string mode)
    {
        if (powerBusy || isPreview || closing) return;
        powerBusy = true; PaintPower(); PowerText("ActionStatus", "正在切换 Windows 电源模式…");
        try
        {
            powerState = await PowerControl.Select(mode); SavePowerSnapshot();
            PowerText("ActionStatus", powerState.Error == "" ? "已确认：Windows 已选择" + mode + "模式" : powerState.Error);
        }
        catch (Exception e) { PowerText("ActionStatus", "切换失败：" + e.Message); }
        finally { powerBusy = false; if (!closing) PaintPower(); }
    }
    private async Task ChangeCharge(int mode)
    {
        if (powerBusy || isPreview || closing) return;
        powerBusy = true; PaintPower(); PowerText("ActionStatus", "正在切换充电模式…");
        try
        {
            chargeState = await PowerControl.ReadCharge(mode); SavePowerSnapshot();
            PowerText("ActionStatus", chargeState.Error == "" && chargeState.Mode == mode ? "已确认：" + chargeState.Label : chargeState.Error == "" ? "尚未确认，请到联想设置检查" : chargeState.Error);
        }
        catch (Exception e) { PowerText("ActionStatus", "切换失败：" + e.Message); }
        finally { powerBusy = false; if (!closing) PaintPower(); }
    }
    private void SavePowerSnapshot()
    {
        try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "power.json"), JsonSerializer.Serialize(new { Power = powerState, Charge = chargeState }, Program.Json)); } catch { }
    }
    private void PaintPower()
    {
        PowerText("BatteryValue", powerState.BatteryPercent.HasValue ? powerState.BatteryPercent + "%" : "—");
        PowerText("Supply", powerState.PluggedIn == true ? (powerState.Charging ? "已接电源 · 正在充电" : "已接电源 · 未在充电") : powerState.PluggedIn == false ? "正在使用电池" : "正在读取电源状态");
        PowerText("SelectedMode", "已选择：" + (powerState.Selected ?? "暂不可用"));
        PowerText("EffectiveMode", powerState.Error != "" ? powerState.Error : powerState.Effective != null && powerState.Effective != powerState.Selected ? "系统当前策略：" + powerState.Effective + "（可能受原厂策略影响）" : "调整 Windows 用电偏好；风扇档位由原厂管理。");
        PowerText("ChargeMode", "当前：" + chargeState.Label);
        PowerText("ChargeHint", chargeState.Error != "" ? chargeState.Error : !chargeState.Supported ? "尚未确认养护支持，可打开联想电池设置。" : chargeState.Storage80 ? "长期插电可选养护，目标约 75–80%。\n已有电量高于上限时，不会主动放电。" : "长期插电可选养护，充电上限由联想固件决定。");
        SetChoice("Efficiency", powerState.Selected == "省电", powerState.Selected != null);
        SetChoice("Balanced", powerState.Selected == "平衡", powerState.Selected != null);
        SetChoice("Performance", powerState.Selected == "性能", powerState.Selected != null);
        SetChoice("Conservation", chargeState.Mode == 1, chargeState.Supported && chargeState.Mode.HasValue && chargeState.Error == "");
        SetChoice("NormalCharge", chargeState.Mode == 0, chargeState.Supported && chargeState.Mode.HasValue && chargeState.Error == "");
        PowerText("PowerUpdated", powerBusy ? "正在读取并确认…" : "打开时刷新 · " + powerState.Time.ToString("HH:mm:ss"));
    }
    private void SetChoice(string name, bool selected, bool supported)
    {
        var button = PowerButton(name); button.IsEnabled = supported && !powerBusy;
        button.Background = new SolidColorBrush(selected ? Color.FromRgb(46, 87, 77) : Color.FromRgb(42, 57, 73));
        button.BorderBrush = new SolidColorBrush(selected ? Mint : Color.FromRgb(67, 83, 101));
        button.Foreground = selected ? new SolidColorBrush(Mint) : new SolidColorBrush(Color.FromRgb(198, 214, 227));
    }
    private void OpenSettings(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { PowerText("ActionStatus", "设置未能打开：" + e.Message); }
    }
    public void RenderPowerPreview(PowerState power, ChargeState charge, string output)
    {
        powerState = power; chargeState = charge; powerView.Visibility = Visibility.Visible; detail.Visibility = Visibility.Collapsed; PaintPower();
        powerView.Measure(new Size(376, 490)); powerView.Arrange(new Rect(0, 0, 376, 490)); powerView.UpdateLayout();
        var bitmap = new RenderTargetBitmap(752, 980, 192, 192, PixelFormats.Pbgra32); bitmap.Render(powerView);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output); encoder.Save(stream);
    }
    private const string PowerXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent">
 <Grid.Resources>
  <Style TargetType="Button">
   <Setter Property="FontSize" Value="11"/><Setter Property="Foreground" Value="#C6D6E3"/><Setter Property="Background" Value="#2A3949"/>
   <Setter Property="BorderBrush" Value="#435365"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="8,8"/><Setter Property="Cursor" Value="Hand"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
    <Border x:Name="Bg" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bg" Property="Opacity" Value="0.8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bg" Property="Opacity" Value="0.42"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bg" Property="BorderBrush" Value="#FFFFFF"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
 </Grid.Resources>
 <Border Margin="9" CornerRadius="22" Background="#F41B2736" BorderBrush="#425263" BorderThickness="0.7">
  <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="3" Opacity="0.3"/></Border.Effect>
  <Grid Margin="18,15">
   <Grid.RowDefinitions><RowDefinition Height="34"/><RowDefinition Height="70"/><RowDefinition Height="14"/><RowDefinition Height="112"/><RowDefinition Height="12"/><RowDefinition Height="125"/><RowDefinition Height="*"/></Grid.RowDefinitions>
   <Button x:Name="Back" Content="‹" FontSize="22" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="26" HorizontalAlignment="Left" VerticalAlignment="Top" ToolTip="返回温度与资源"/>
   <TextBlock Text="电源与电池" Foreground="#ECF4F9" FontSize="17" FontWeight="SemiBold" Margin="32,0,0,0"/>
   <Button x:Name="ClosePower" Content="×" FontSize="19" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="25" HorizontalAlignment="Right" VerticalAlignment="Top"/>
   <Border Grid.Row="1" Background="#263E42" CornerRadius="13" Padding="14,8">
    <Grid><TextBlock x:Name="BatteryValue" Text="—" Foreground="#DFFAF0" FontFamily="Segoe UI" FontSize="30" FontWeight="SemiBold" VerticalAlignment="Center"/>
     <StackPanel Margin="110,0,0,0"><TextBlock Text="BATTERY" Foreground="#82B5AA" FontSize="9"/><TextBlock x:Name="Supply" Foreground="#B3D6CE" FontSize="10" Margin="0,3,0,0" TextWrapping="Wrap"/><Button x:Name="OpenScreen" Content="屏幕熄灭时间  ›" Foreground="#67E7C1" FontSize="10" Background="Transparent" BorderThickness="0" Padding="0,3" HorizontalAlignment="Left" Margin="0,2,0,0"/></StackPanel>
    </Grid>
   </Border>
   <StackPanel Grid.Row="3">
    <Grid><TextBlock Text="Windows 电源模式" Foreground="#E3ECF2" FontSize="12" FontWeight="SemiBold"/><TextBlock x:Name="SelectedMode" Foreground="#8EABC0" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Center"/></Grid>
    <UniformGrid Columns="3" Margin="-3,10,-3,8"><Button x:Name="Efficiency" Content="省电" Margin="3,0"/><Button x:Name="Balanced" Content="平衡" Margin="3,0"/><Button x:Name="Performance" Content="性能" Margin="3,0"/></UniformGrid>
    <TextBlock x:Name="EffectiveMode" Foreground="#829CB0" FontSize="10" TextWrapping="Wrap" LineHeight="15"/>
   </StackPanel>
   <StackPanel Grid.Row="5">
    <Grid><TextBlock Text="电池养护" Foreground="#E3ECF2" FontSize="12" FontWeight="SemiBold"/><TextBlock x:Name="ChargeMode" Foreground="#8EABC0" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Center"/></Grid>
    <UniformGrid Columns="2" Margin="-3,10,-3,8"><Button x:Name="Conservation" Content="养护充电" Margin="3,0" ToolTip="限制充电上限，适合长期插电"/><Button x:Name="NormalCharge" Content="正常充电" Margin="3,0" ToolTip="恢复正常充电，允许充至 100%"/></UniformGrid>
    <TextBlock x:Name="ChargeHint" Foreground="#829CB0" FontSize="10" TextWrapping="Wrap" LineHeight="16"/>
   </StackPanel>
   <StackPanel Grid.Row="6" VerticalAlignment="Bottom">
    <TextBlock x:Name="ActionStatus" Text="点击即切换 · 成功后显示确认结果" Foreground="#A5BECE" FontSize="10" TextWrapping="Wrap" MaxHeight="32"/>
    <Grid Margin="0,6,0,0"><Button x:Name="WindowsSettings" Content="系统电源设置 ↗" FontSize="9" Background="Transparent" BorderThickness="0" Padding="0,3" HorizontalAlignment="Left"/><Button x:Name="LenovoSettings" Content="联想电池设置 ↗" FontSize="9" Background="Transparent" BorderThickness="0" Padding="0,3" HorizontalAlignment="Right"/></Grid>
    <TextBlock x:Name="PowerUpdated" Foreground="#68869C" FontSize="9" Margin="0,3,0,0"/>
   </StackPanel>
  </Grid>
 </Border>
</Grid>
""";
}

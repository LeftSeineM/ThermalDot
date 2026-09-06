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

namespace ThermalDot;

public sealed partial class DotWindow
{
    private Grid screenView = null!;
    private DisplayTimeoutState screenState = new();
    private bool screenBusy, loadingScreen;
    private ComboBox ScreenChoice(string name) => (ComboBox)screenView.FindName(name);
    private Button ScreenButton(string name) => (Button)screenView.FindName(name);
    private void ScreenText(string name, string value)
    {
        var text = (TextBlock)screenView.FindName(name); text.Text = value; text.ToolTip = value;
    }
    private bool IsScreenDropDownOpen => screenView != null &&
        (ScreenChoice("AcTimeout").IsDropDownOpen || ScreenChoice("DcTimeout").IsDropDownOpen);

    private void InitializeScreenView(Grid pages)
    {
        screenView = (Grid)XamlReader.Parse(ScreenXaml);
        screenView.Visibility = Visibility.Collapsed; pages.Children.Add(screenView);
        ScreenButton("BackScreen").Click += (_, _) => ShowPowerView();
        ScreenButton("CloseScreen").Click += (_, _) => panel.Hide();
        ScreenButton("ApplyScreen").Click += async (_, _) => await ApplyScreenTimeout();
        ScreenButton("ReloadScreen").Click += (_, _) => LoadScreen(DisplayTimeout.Read());
        ScreenButton("ScreenSettings").Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); }
            catch (Exception e) { ScreenText("ScreenStatus", "设置未能打开：" + e.Message); }
        };
        ScreenChoice("AcTimeout").SelectionChanged += (_, _) => ScreenSelectionChanged();
        ScreenChoice("DcTimeout").SelectionChanged += (_, _) => ScreenSelectionChanged();
    }
    private void ShowScreenView()
    {
        detail.Visibility = Visibility.Collapsed; powerView.Visibility = Visibility.Collapsed;
        screenView.Visibility = Visibility.Visible;
        if (!screenBusy) LoadScreen(isPreview ? screenState : DisplayTimeout.Read());
    }
    private void LoadScreen(DisplayTimeoutState state)
    {
        screenState = state; loadingScreen = true;
        Populate("AcTimeout", state.AcSeconds); Populate("DcTimeout", state.DcSeconds);
        loadingScreen = false;
        ScreenText("AcCurrent", "当前：" + DisplayTimeout.Label(state.AcSeconds));
        ScreenText("DcCurrent", "当前：" + DisplayTimeout.Label(state.DcSeconds));
        ScreenText("ScreenStatus", state.Error != "" ? state.Error : "选择时长后，点击应用设置。");
        UpdateScreenControls();
        void Populate(string name, uint? seconds)
        {
            var box = ScreenChoice(name);
            box.ItemsSource = DisplayTimeout.Choices(seconds);
            box.SelectedValue = seconds;
        }
    }
    private bool HasScreenChanges => ScreenChoice("AcTimeout").SelectedValue is uint ac &&
        ScreenChoice("DcTimeout").SelectedValue is uint dc &&
        (ac != screenState.AcSeconds || dc != screenState.DcSeconds);
    private void ScreenSelectionChanged()
    {
        if (loadingScreen) return;
        ScreenText("ScreenStatus", HasScreenChanges ? "尚未应用 · 点击下方按钮保存到 Windows。" : "与当前设置一致。");
        UpdateScreenControls();
    }
    private void UpdateScreenControls()
    {
        ScreenChoice("AcTimeout").IsEnabled = screenState.Available && !screenBusy;
        ScreenChoice("DcTimeout").IsEnabled = screenState.Available && !screenBusy;
        ScreenButton("ApplyScreen").IsEnabled = screenState.Available && HasScreenChanges && !screenBusy;
        ScreenButton("ReloadScreen").IsEnabled = !screenBusy;
        ScreenButton("BackScreen").IsEnabled = !screenBusy;
    }
    private async Task ApplyScreenTimeout()
    {
        if (screenBusy || isPreview || closing || !screenState.Available ||
            ScreenChoice("AcTimeout").SelectedValue is not uint ac || ScreenChoice("DcTimeout").SelectedValue is not uint dc) return;
        screenBusy = true; UpdateScreenControls(); ScreenText("ScreenStatus", "正在保存并确认…");
        try
        {
            var after = await Task.Run(() => DisplayTimeout.Apply(screenState, ac, dc));
            if (closing) return;
            // Keep operation errors in the status, while allowing a retry after a successful fresh read.
            LoadScreen(DisplayTimeout.Read());
            ScreenText("ScreenStatus", after.Error != "" ? after.Error :
                "已保存 · 插电 " + DisplayTimeout.Label(after.AcSeconds) + " / 电池 " + DisplayTimeout.Label(after.DcSeconds));
            try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "screen.json"), JsonSerializer.Serialize(after, Program.Json)); } catch { }
        }
        catch (Exception e) { if (!closing) ScreenText("ScreenStatus", "未能应用：" + e.Message); }
        finally { screenBusy = false; if (!closing) UpdateScreenControls(); }
    }

    public void RenderScreenPreview(DisplayTimeoutState state, string output)
    {
        powerView.Visibility = Visibility.Collapsed; detail.Visibility = Visibility.Collapsed; screenView.Visibility = Visibility.Visible;
        LoadScreen(state);
        screenView.Measure(new Size(376, 490)); screenView.Arrange(new Rect(0, 0, 376, 490)); screenView.UpdateLayout();
        var bitmap = new RenderTargetBitmap(752, 980, 192, 192, PixelFormats.Pbgra32); bitmap.Render(screenView);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output); encoder.Save(stream);
    }
    private const string ScreenXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent">
 <Grid.Resources>
  <Style TargetType="Button">
   <Setter Property="FontSize" Value="11"/><Setter Property="Foreground" Value="#C6D6E3"/><Setter Property="Background" Value="#2A3949"/><Setter Property="BorderBrush" Value="#435365"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="10,8"/><Setter Property="Cursor" Value="Hand"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
    <Border x:Name="Bg" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="9" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bg" Property="Opacity" Value="0.8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bg" Property="Opacity" Value="0.42"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Bg" Property="BorderBrush" Value="White"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ComboBoxItem">
   <Setter Property="Foreground" Value="#DFEBF4"/><Setter Property="Padding" Value="12,8"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
    <Border x:Name="ItemBg" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
    <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemBg" Property="Background" Value="#355D55"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="#67E7C1"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ComboBox">
   <Setter Property="Foreground" Value="#EDF6FB"/><Setter Property="FontSize" Value="13"/><Setter Property="Height" Value="39"/><Setter Property="DisplayMemberPath" Value="Label"/><Setter Property="SelectedValuePath" Value="Seconds"/><Setter Property="MaxDropDownHeight" Value="235"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
    <Grid>
     <ToggleButton Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
      <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Field" Background="#263A4B" BorderBrush="#4A6377" BorderThickness="1" CornerRadius="9"><TextBlock Text="⌄" Foreground="#67E7C1" FontSize="16" Margin="0,0,12,0" HorizontalAlignment="Right" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Field" Property="BorderBrush" Value="#67E7C1"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Field" Property="BorderBrush" Value="#67E7C1"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
     </ToggleButton>
     <ContentPresenter Margin="12,0,32,0" VerticalAlignment="Center" IsHitTestVisible="False" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
     <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
      <Border Background="#223344" BorderBrush="#59758A" BorderThickness="1" CornerRadius="9" Padding="4" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
       <ScrollViewer MaxHeight="{TemplateBinding MaxDropDownHeight}" CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
      </Border>
     </Popup>
    </Grid>
    <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="Foreground" Value="#67E7C1"/></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
 </Grid.Resources>
 <Border Margin="9" CornerRadius="22" Background="#F41B2736" BorderBrush="#425263" BorderThickness="0.7">
  <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="3" Opacity="0.3"/></Border.Effect>
  <Grid Margin="18,15">
   <Grid.RowDefinitions><RowDefinition Height="36"/><RowDefinition Height="42"/><RowDefinition Height="89"/><RowDefinition Height="12"/><RowDefinition Height="89"/><RowDefinition Height="52"/><RowDefinition Height="42"/><RowDefinition Height="*"/></Grid.RowDefinitions>
   <Button x:Name="BackScreen" Content="‹" FontSize="22" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="26" HorizontalAlignment="Left" VerticalAlignment="Top" ToolTip="返回电源与电池"/>
   <TextBlock Text="屏幕熄灭" Foreground="#ECF4F9" FontSize="17" FontWeight="SemiBold" Margin="32,0,0,0"/>
   <Button x:Name="CloseScreen" Content="×" FontSize="19" Background="Transparent" BorderThickness="0" Padding="0" Width="25" Height="25" HorizontalAlignment="Right" VerticalAlignment="Top"/>
   <TextBlock Grid.Row="1" Text="停止操作后，多久自动关闭屏幕" Foreground="#91AFC2" FontSize="12" Margin="0,5,0,0"/>
   <Border Grid.Row="2" Background="#233A40" CornerRadius="13" Padding="13,11">
    <StackPanel><Grid Margin="0,0,0,9"><TextBlock Text="接通电源" Foreground="#D8EEE7" FontSize="12" FontWeight="SemiBold"/><TextBlock x:Name="AcCurrent" Foreground="#8FBAAE" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Center"/></Grid><ComboBox x:Name="AcTimeout" AutomationProperties.Name="接通电源时关闭屏幕的时间"/></StackPanel>
   </Border>
   <Border Grid.Row="4" Background="#233443" CornerRadius="13" Padding="13,11">
    <StackPanel><Grid Margin="0,0,0,9"><TextBlock Text="使用电池" Foreground="#DCE8F3" FontSize="12" FontWeight="SemiBold"/><TextBlock x:Name="DcCurrent" Foreground="#8CAAC1" FontSize="10" HorizontalAlignment="Right" VerticalAlignment="Center"/></Grid><ComboBox x:Name="DcTimeout" AutomationProperties.Name="使用电池时关闭屏幕的时间"/></StackPanel>
   </Border>
   <TextBlock Grid.Row="5" Text="这里调整自动熄屏时间。电脑是否睡眠，&#10;仍由 Windows 的睡眠设置决定。" Foreground="#829CB0" FontSize="10" LineHeight="16" Margin="0,12,0,0"/>
   <Button Grid.Row="6" x:Name="ApplyScreen" Content="应用设置" Background="#2E574D" BorderBrush="#67E7C1" Foreground="#9AF3D7" FontSize="12" FontWeight="SemiBold"/>
   <Grid Grid.Row="7">
    <TextBlock x:Name="ScreenStatus" Foreground="#A5BECE" FontSize="10" TextWrapping="Wrap" Margin="0,10,0,27" LineHeight="14"/>
    <Button x:Name="ReloadScreen" Content="重新读取" Background="Transparent" BorderThickness="0" Padding="0,3" FontSize="10" VerticalAlignment="Bottom" HorizontalAlignment="Left"/>
    <Button x:Name="ScreenSettings" Content="系统屏幕与睡眠设置 ↗" Background="Transparent" BorderThickness="0" Padding="0,3" FontSize="10" VerticalAlignment="Bottom" HorizontalAlignment="Right"/>
   </Grid>
  </Grid>
 </Border>
</Grid>
""";
}
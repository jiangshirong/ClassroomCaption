using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassroomCaption.Core;

namespace ClassroomCaption.App;

public sealed class SettingsWindow : Window
{
    private readonly SettingsStore store;
    private readonly TextBox workspace, output;
    private readonly ComboBox region;
    private readonly TextBox aliyun = new();
    private readonly TextBlock status = UiKit.Label("");
    private readonly List<Button> actions = [];
    private bool working;
    public AppSettings Settings { get; private set; }
    public ApiCredentials? Credentials { get; private set; }
    public bool Saved { get; private set; }

    public SettingsWindow(SettingsStore store, AppSettings settings, ApiCredentials? credentials)
    {
        this.store = store; Settings = settings.Clone(); Credentials = credentials;
        if (Credentials is null)
        {
            try { Credentials = store.LoadCredentials(); }
            catch (Exception ex) { status.Text = SafeErrors.Describe(ex); }
        }
        NameScope.SetNameScope(this, new NameScope());
        AutomationProperties.SetName(aliyun, "阿里云 API Key");
        RegisterName("AliyunApiKey", aliyun);
        RegisterName("CredentialStatus", status);
        LoadKeys();
        var tabs = new TabControl { Margin = new(26, 8, 26, 0) };
        RegisterName("SettingsTabs", tabs);
        var connection = AddTab(tabs, "模型连接");
        var keysPanel = new StackPanel();
        keysPanel.Children.Add(ProviderHeader("阿里云百炼"));
        keysPanel.Children.Add(FieldLabel("API Key")); keysPanel.Children.Add(aliyun);
        connection.Children.Add(UiKit.Card(keysPanel));
        connection.Children.Add(new TextBlock { Text = "密钥明文保存在本机。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = UiKit.Brush("#B7CFC6"), Margin = new(2, 10, 0, 0) });

        var advanced = new StackPanel();
        advanced.Children.Add(FieldLabel("阿里云地域"));
        region = new ComboBox { ItemsSource = new[] { "北京", "新加坡" }, SelectedIndex = settings.Region == "ap-southeast-1" ? 1 : 0, Margin = new(0, 6, 0, 14) };
        AutomationProperties.SetName(region, "阿里云地域");
        advanced.Children.Add(region);
        workspace = Field(advanced, "Workspace ID · LiveTranslate 必填", settings.WorkspaceId);
        workspace.ToolTip = "填写与阿里云 Key 同地域的业务空间编号。";
        var advancedExpander = new Expander { Header = "高级连接设置", Content = UiKit.Card(advanced) };
        RegisterName("AdvancedConnection", advancedExpander); connection.Children.Add(advancedExpander);

        var data = AddTab(tabs, "课堂设置");
        var dataPanel = new StackPanel();
        dataPanel.Children.Add(UiKit.Label("只保存文字，不保存音频。"));
        output = Field(dataPanel, "文字保存位置", settings.OutputDirectory);
        RegisterName("OutputDirectory", output);
        AddButton(dataPanel, "选择文件夹", (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择文字保存位置" };
            if (picker.ShowDialog(this) == true) output.Text = picker.FolderName;
        });
        AddButton(dataPanel, "导出已有课堂记录", ExportExistingClick);
        data.Children.Add(UiKit.Card(dataPanel));

        var footer = new Grid { Margin = new(26, 14, 26, 22) };
        footer.RowDefinitions.Add(new() { Height = GridLength.Auto }); footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        status.Margin = new(0, 0, 0, 10); footer.Children.Add(status);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new()); buttons.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var test = UiKit.Button("测试连接（可能计费）", TestClick); test.HorizontalAlignment = HorizontalAlignment.Left; test.ToolTip = "向 LiveTranslate 提交少量静音，验证连接；会产生少量模型用量。"; buttons.Children.Add(test); actions.Add(test);
        RegisterName("TestConnectionButton", test);
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) test.Visibility = tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; };
        var save = UiKit.Button("保存", SaveClick, true); Grid.SetColumn(save, 1); buttons.Children.Add(save); actions.Add(save);
        RegisterName("SaveSettingsButton", save);
        Grid.SetRow(buttons, 1); footer.Children.Add(buttons);
        UiKit.Shell(this, "连接与设置", tabs, 620, 700, footer);
        Closing += (_, e) =>
        {
            if (working) { e.Cancel = true; status.Text = "正在连接，请稍候…"; }
            else { aliyun.Clear(); }
        };
    }
    private void LoadKeys()
    {
        aliyun.Text = Credentials?.AliyunKey ?? "";
    }
    private static StackPanel AddTab(TabControl tabs, string title)
    {
        var panel = new StackPanel { Margin = new(0, 0, 5, 5) };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return panel;
    }
    private static UIElement ProviderHeader(string name) => new TextBlock
    { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) };
    private static TextBlock FieldLabel(string text) => new() { Text = text, FontSize = 12, Foreground = UiKit.Brush("#ADC0C1"), Margin = new(0, 7, 0, 0) };
    private static TextBox Field(Panel panel, string title, string value) { panel.Children.Add(FieldLabel(title)); var box = new TextBox { Text = value }; AutomationProperties.SetName(box, title); panel.Children.Add(box); return box; }
    private void AddButton(Panel panel, string title, RoutedEventHandler handler)
    {
        var button = UiKit.Button(title, handler); button.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(button); actions.Add(button);
    }
    private AppSettings ReadSettings()
    {
        var result = Settings.Clone();
        result.Region = region.SelectedIndex == 1 ? "ap-southeast-1" : "cn-beijing";
        result.WorkspaceId = workspace.Text.Trim();
        if (string.IsNullOrWhiteSpace(output.Text)) throw new UserFacingException("请选择文字保存位置。");
        result.OutputDirectory = Path.GetFullPath(output.Text.Trim());
        result.Validate();
        _ = LiveTranslateProtocol.Terms(result.Glossary);
        SettingsStore.CheckWritable(result.OutputDirectory);
        return result;
    }
    private ApiCredentials ReadKeys()
    {
        var result = new ApiCredentials(aliyun.Text.Trim());
        if (!result.Complete) throw new UserFacingException("请填写阿里云 API Key。");
        return result;
    }
    private void SetWorking(bool value) { working = value; foreach (var button in actions) button.IsEnabled = !value; }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (working) return; SetWorking(true);
        try
        {
            var candidate = ReadSettings();
            var hasKeys = !string.IsNullOrWhiteSpace(aliyun.Text);
            var keys = hasKeys ? ReadKeys() : null;
            if (keys is not null) store.SaveCredentials(keys);
            store.Save(candidate); Settings = candidate; Credentials = keys; Saved = true;
            SetWorking(false); Close();
        }
        catch (Exception ex) { status.Text = SafeErrors.Describe(ex); }
        finally { SetWorking(false); }
    }
    private async void TestClick(object sender, RoutedEventArgs e)
    {
        if (working) return; SetWorking(true);
        try
        {
            var candidate = ReadSettings(); var keys = ReadKeys();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            status.Text = "测试中…";
            await using (var client = new LiveTranslateClient(candidate, keys.AliyunKey))
            {
                await client.StartAsync(cancellation.Token);
                for (var i = 0; i < 10; i++) { client.Enqueue(new byte[3200]); await Task.Delay(100, cancellation.Token); }
                await client.StopAsync();
            }
            Credentials = keys;
            status.Text = "连接成功。";
        }
        catch (Exception ex) { status.Text = SafeErrors.Describe(ex); }
        finally { SetWorking(false); }
    }
    private void ExportExistingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = new Microsoft.Win32.OpenFileDialog { Filter = "课堂记录 (*.jsonl)|*.jsonl", InitialDirectory = Settings.OutputDirectory };
            if (input.ShowDialog(this) != true) return;
            var target = new Microsoft.Win32.SaveFileDialog { Filter = "双语文字 (*.txt)|*.txt", FileName = Path.GetFileNameWithoutExtension(input.FileName) + ".txt" };
            if (target.ShowDialog(this) == true) { SessionStore.ExportText(input.FileName, target.FileName); status.Text = "已导出。"; }
        }
        catch (Exception ex) { status.Text = SafeErrors.Describe(ex); }
    }
}

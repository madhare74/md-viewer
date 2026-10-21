using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MdViewer.Core.Settings;
using MdViewer.Services;

namespace MdViewer.Dialogs;

/// <summary>Окно «Настройки». Подписи стоят над полями, чтобы при крупном масштабе ничего не обрезалось.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ComboBox _theme = new();
    private readonly ComboBox _font = new() { IsEditable = true };
    private readonly ComboBox _fontSize = new() { IsEditable = true };
    private readonly ComboBox _zoom = new();
    private readonly ComboBox _mode = new();
    private readonly CheckBox _fullWidth = new() { Content = new TextBlock { Text = "Текст во всю ширину окна", TextWrapping = TextWrapping.Wrap } };
    private readonly CheckBox _wrap = new() { Content = new TextBlock { Text = "Перенос длинных строк в редакторе", TextWrapping = TextWrapping.Wrap } };
    private readonly CheckBox _assoc = new() { Content = new TextBlock { Text = "Открывать файлы .md и .markdown в MD Viewer", TextWrapping = TextWrapping.Wrap } };

    private static readonly (string Label, ThemeSetting Value)[] Themes =
    [
        ("Как в системе", ThemeSetting.System),
        ("Светлая", ThemeSetting.Light),
        ("Тёмная", ThemeSetting.Dark),
    ];

    private static readonly (string Label, ViewMode Value)[] Modes =
    [
        ("Просмотр", ViewMode.View),
        ("Правка и просмотр", ViewMode.Split),
        ("Правка", ViewMode.Edit),
    ];

    private static readonly double[] Zooms = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        Title = "Настройки";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        AutomationProperties.SetAutomationId(this, "SettingsWindow");

        foreach (var t in Themes)
        {
            _theme.Items.Add(t.Label);
        }

        _theme.SelectedIndex = Array.FindIndex(Themes, t => t.Value == settings.Theme);

        foreach (string f in new[] { "Consolas", "Cascadia Mono", "Cascadia Code", "Courier New", "Lucida Console", "JetBrains Mono" })
        {
            _font.Items.Add(f);
        }

        _font.Text = settings.EditorFontFamily;

        foreach (int s in new[] { 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28 })
        {
            _fontSize.Items.Add(s.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _fontSize.Text = settings.EditorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (double z in Zooms)
        {
            _zoom.Items.Add($"{Math.Round(z * 100)} %");
        }

        int zi = Array.FindIndex(Zooms, z => Math.Abs(z - settings.PreviewZoom) < 0.001);
        if (zi < 0)
        {
            _zoom.Items.Add($"{Math.Round(settings.PreviewZoom * 100)} %");
            zi = _zoom.Items.Count - 1;
        }

        _zoom.SelectedIndex = zi;

        foreach (var m in Modes)
        {
            _mode.Items.Add(m.Label);
        }

        _mode.SelectedIndex = Array.FindIndex(Modes, m => m.Value == settings.DefaultMode);
        _fullWidth.IsChecked = settings.FullWidth;
        _wrap.IsChecked = settings.WordWrap;
        _assoc.IsChecked = FileAssociation.IsRegistered();

        AutomationProperties.SetAutomationId(_theme, "ThemeCombo");
        AutomationProperties.SetAutomationId(_font, "FontCombo");
        AutomationProperties.SetAutomationId(_fontSize, "FontSizeCombo");
        AutomationProperties.SetAutomationId(_zoom, "ZoomCombo");
        AutomationProperties.SetAutomationId(_mode, "ModeCombo");
        AutomationProperties.SetAutomationId(_fullWidth, "FullWidthCheck");
        AutomationProperties.SetAutomationId(_wrap, "WrapCheck");
        AutomationProperties.SetAutomationId(_assoc, "AssocCheck");

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Section("Оформление"));
        root.Children.Add(Field("Тема", _theme));
        root.Children.Add(Field("Масштаб просмотра", _zoom));
        root.Children.Add(Spaced(_fullWidth));
        root.Children.Add(Section("Редактор"));
        root.Children.Add(Field("Шрифт редактора", _font));
        root.Children.Add(Field("Размер шрифта редактора", _fontSize));
        root.Children.Add(Spaced(_wrap));
        root.Children.Add(Section("Открытие файлов"));
        root.Children.Add(Field("Режим при открытии файла", _mode));
        root.Children.Add(Spaced(_assoc));
        var hint = new TextBlock
        {
            Text = "Если файлы .md уже открываются другой программой, Windows может спросить, чем их открывать. Выберите MD Viewer и отметьте «Всегда».",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(22, 2, 0, 6),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        root.Children.Add(hint);
        var defaults = new Button
        {
            Content = new TextBlock { Text = "Приложения по умолчанию в Windows…" },
            Style = (Style)Application.Current.FindResource("DialogButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(22, 0, 0, 12),
        };
        defaults.Click += (_, _) => App.OpenUrl("ms-settings:defaultapps");
        root.Children.Add(defaults);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Сохранить", IsDefault = true, MinWidth = 100, Margin = new Thickness(8, 4, 0, 0), Style = (Style)Application.Current.FindResource("AccentButton") };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 100, Margin = new Thickness(8, 4, 0, 0), Style = (Style)Application.Current.FindResource("DialogButton") };
        AutomationProperties.SetAutomationId(ok, "SettingsOk");
        AutomationProperties.SetAutomationId(cancel, "SettingsCancel");
        ok.Click += (_, _) => Apply();
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        App.ApplyUiScale(root);
        Width *= App.UiScale;
        MaxHeight = SystemParameters.WorkArea.Height;
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
        return t;
    }

    private static FrameworkElement Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var l = new Label { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Target = control, Padding = new Thickness(0, 0, 0, 3) };
        l.SetResourceReference(ForegroundProperty, "Brush.Text");
        AutomationProperties.SetName(control, label);
        control.MinHeight = 26;
        panel.Children.Add(l);
        panel.Children.Add(control);
        return panel;
    }

    private static FrameworkElement Spaced(CheckBox c)
    {
        c.Margin = new Thickness(0, 2, 0, 8);
        c.SetResourceReference(ForegroundProperty, "Brush.Text");
        return c;
    }

    private void Apply()
    {
        if (_theme.SelectedIndex >= 0)
        {
            _settings.Theme = Themes[_theme.SelectedIndex].Value;
        }

        if (!string.IsNullOrWhiteSpace(_font.Text))
        {
            _settings.EditorFontFamily = _font.Text.Trim();
        }

        if (double.TryParse(_fontSize.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
        {
            _settings.EditorFontSize = Math.Clamp(size, 8, 48);
        }

        if (_zoom.SelectedIndex >= 0 && _zoom.SelectedIndex < Zooms.Length)
        {
            _settings.PreviewZoom = Zooms[_zoom.SelectedIndex];
        }

        if (_mode.SelectedIndex >= 0)
        {
            _settings.DefaultMode = Modes[_mode.SelectedIndex].Value;
        }

        _settings.FullWidth = _fullWidth.IsChecked == true;
        _settings.WordWrap = _wrap.IsChecked == true;

        bool wantAssoc = _assoc.IsChecked == true;
        try
        {
            if (wantAssoc && !FileAssociation.IsRegistered())
            {
                FileAssociation.Register();
            }
            else if (!wantAssoc && FileAssociation.IsRegistered())
            {
                FileAssociation.Unregister();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось изменить связь с файлами", ex);
            ChoiceDialog.Info(this, "Не удалось изменить связь с файлами .md.", ex.Message);
        }

        DialogResult = true;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MdViewer.Services;

namespace MdViewer.Dialogs;

/// <summary>Кнопка диалога: подпись, AutomationId, кнопка по умолчанию (Enter) и отмены (Esc).</summary>
public sealed record DialogButton(string Label, string AutomationId, bool IsDefault = false, bool IsCancel = false);

/// <summary>
/// Простой диалог с сообщением и кнопками на русском языке (вместо MessageBox, у которого подписи кнопок
/// зависят от языка Windows). Текст переносится по словам, окно растёт по содержимому.
/// </summary>
public sealed class ChoiceDialog : Window
{
    private int _result = -1;

    private ChoiceDialog(string title, string message, string? details, IReadOnlyList<DialogButton> buttons)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MinWidth = 360;
        MaxWidth = 640;
        SetResourceReference(BackgroundProperty, "Brush.Window");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        AutomationProperties.SetAutomationId(this, "ChoiceDialog");

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            Margin = new Thickness(0, 0, 0, details is null ? 18 : 8),
        };
        AutomationProperties.SetAutomationId(text, "DialogMessage");
        root.Children.Add(text);
        if (details is not null)
        {
            var d = new TextBlock
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                Margin = new Thickness(0, 0, 0, 18),
                FontSize = 12.5,
            };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
            root.Children.Add(d);
        }

        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            int index = i;
            var btn = new Button
            {
                Content = b.Label,
                IsDefault = b.IsDefault,
                IsCancel = b.IsCancel,
                MinWidth = 100,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(8, 4, 0, 0),
                Style = (Style)Application.Current.FindResource(b.IsDefault ? "AccentButton" : "DialogButton"),
            };
            AutomationProperties.SetAutomationId(btn, b.AutomationId);
            btn.Click += (_, _) =>
            {
                _result = index;
                DialogResult = true;
            };
            panel.Children.Add(btn);
        }

        root.Children.Add(panel);
        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>Показывает диалог. Возвращает индекс нажатой кнопки или индекс кнопки отмены при закрытии окна.</summary>
    public static int Show(Window? owner, string message, IReadOnlyList<DialogButton> buttons, string? details = null, string title = "MD Viewer")
    {
        var dlg = new ChoiceDialog(title, message, details, buttons);
        if (owner is not null && owner.IsLoaded)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ShowDialog();
        if (dlg._result >= 0)
        {
            return dlg._result;
        }

        int cancel = buttons.ToList().FindIndex(b => b.IsCancel);
        return cancel >= 0 ? cancel : buttons.Count - 1;
    }

    public static void Info(Window? owner, string message, string? details = null) =>
        Show(owner, message, [new DialogButton("ОК", "OkButton", IsDefault: true, IsCancel: true)], details);

    public static bool Confirm(Window? owner, string message, string yes, string no = "Отмена", string? details = null) =>
        Show(owner, message, [new DialogButton(yes, "YesButton", IsDefault: true), new DialogButton(no, "NoButton", IsCancel: true)], details) == 0;
}

/// <summary>Ответ на вопрос о несохранённых изменениях.</summary>
public enum SaveChoice
{
    Save,
    DontSave,
    Cancel,
}

public static class SaveChangesDialog
{
    public static SaveChoice Ask(Window owner, string fileName)
    {
        int r = ChoiceDialog.Show(owner, $"Сохранить изменения в файле «{fileName}»?",
        [
            new DialogButton("Сохранить", "SaveButton", IsDefault: true),
            new DialogButton("Не сохранять", "DontSaveButton"),
            new DialogButton("Отмена", "CancelButton", IsCancel: true),
        ]);
        return r switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.DontSave,
            _ => SaveChoice.Cancel,
        };
    }
}

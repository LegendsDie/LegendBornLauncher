using System;
using System.Windows;
using System.Windows.Input;

namespace LegendBorn.Views;

public partial class LauncherUpdateDialog : Window
{
    private readonly bool _progressMode;

    public LauncherUpdateDialog(
        string title,
        string message,
        string currentVersion,
        string targetVersion,
        string source,
        bool progressMode = false,
        bool error = false)
    {
        InitializeComponent();

        _progressMode = progressMode;
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Обновление LegendBorn" : title.Trim();
        MessageText.Text = (message ?? string.Empty).Trim();
        CurrentVersionText.Text = string.IsNullOrWhiteSpace(currentVersion) ? "—" : currentVersion.Trim();
        TargetVersionText.Text = string.IsNullOrWhiteSpace(targetVersion) ? "—" : targetVersion.Trim();
        SourceText.Text = string.IsNullOrWhiteSpace(source) ? "LegendBorn" : source.Trim();

        if (error)
            TitleText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 176, 190));

        if (_progressMode)
        {
            ProgressArea.Visibility = Visibility.Visible;
            ActionButtons.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Collapsed;
        }
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string currentVersion,
        string targetVersion,
        string source,
        string primaryText = "Обновить")
    {
        var dialog = new LauncherUpdateDialog(
            title,
            message,
            currentVersion,
            targetVersion,
            source);

        dialog.PrimaryButton.Content = primaryText;
        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;

        return dialog.ShowDialog() == true;
    }

    public static void ShowMessage(
        Window? owner,
        string title,
        string message,
        string currentVersion,
        string targetVersion = "—",
        bool error = false)
    {
        var dialog = new LauncherUpdateDialog(
            title,
            message,
            currentVersion,
            targetVersion,
            "LegendBorn",
            progressMode: false,
            error: error);

        dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        dialog.PrimaryButton.Content = "Понятно";
        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;
        _ = dialog.ShowDialog();
    }

    public static LauncherUpdateDialog CreateProgress(
        Window? owner,
        string currentVersion,
        string targetVersion,
        string source)
    {
        var dialog = new LauncherUpdateDialog(
            "Обновляем LegendBorn",
            "Можно оставить окно открытым — лаунчер проверит пакет и подготовит перезапуск автоматически.",
            currentVersion,
            targetVersion,
            source,
            progressMode: true);

        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;
        return dialog;
    }

    public void SetProgress(int percent, string? status = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        UpdateProgressBar.Value = percent;
        ProgressPercentText.Text = $"{percent}%";
        if (!string.IsNullOrWhiteSpace(status))
            ProgressStatusText.Text = status.Trim();
    }

    public void SetSource(string source)
    {
        if (!string.IsNullOrWhiteSpace(source))
            SourceText.Text = source.Trim();
    }

    public void SetStatus(string status)
    {
        if (!string.IsNullOrWhiteSpace(status))
            ProgressStatusText.Text = status.Trim();
    }

    private void PrimaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_progressMode) return;
        DialogResult = true;
        Close();
    }

    private void SecondaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_progressMode) return;
        DialogResult = false;
        Close();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_progressMode) return;
        DialogResult = false;
        Close();
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}

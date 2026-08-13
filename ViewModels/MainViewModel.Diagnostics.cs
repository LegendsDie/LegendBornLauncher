using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private AsyncRelayCommand? _collectDiagnosticsCommand;

    public AsyncRelayCommand CollectDiagnosticsCommand =>
        _collectDiagnosticsCommand ??= new AsyncRelayCommand(
            CollectDiagnosticsAsync,
            () => !_isClosing,
            ex =>
            {
                AppendLog($"Диагностика: {ex.GetType().Name}: {ex.Message}");
                StatusText = "Не удалось собрать диагностический отчёт.";
            });

    private async Task CollectDiagnosticsAsync(CancellationToken ct)
    {
        if (_isClosing) return;

        StatusText = "Собираю диагностический отчёт...";
        AppendLog("Диагностика: сбор безопасного отчёта...");

        var server = SelectedServer;
        var context = new DiagnosticReportService.ReportContext(
            GameDir: _gameDir,
            ServerId: server?.Id,
            ServerName: server?.Name,
            ServerAddress: server?.Address,
            MinecraftVersion: server?.MinecraftVersion,
            LoaderName: server?.LoaderName,
            LoaderVersion: server?.LoaderVersion,
            RamMb: RamMb,
            AutoConnect: AutoConnect);

        var path = await DiagnosticReportService.CreateAsync(context, ct);

        AppendLog($"Диагностика: отчёт готов — {path}");
        StatusText = "Диагностический отчёт готов.";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // The report exists even if Explorer could not be opened.
        }
    }
}

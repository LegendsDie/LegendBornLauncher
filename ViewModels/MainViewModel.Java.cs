using System;
using System.IO;
using System.Threading.Tasks;
using LegendBorn.Mvvm;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private const string JavaCustomSentinel = "@custom";
    private readonly JavaRuntimeService _javaRuntime = new();
    private bool _isJavaBusy;
    private string _javaStatusText = "Java будет проверена перед запуском.";
    private string? _resolvedJavaPath;
    private string _lastCustomJavaPath = string.Empty;
    private AsyncRelayCommand? _checkJavaCommand;

    public string JavaMode
    {
        get
        {
            try
            {
                var raw = (_config.Current.JavaPath ?? string.Empty).Trim();
                if (raw.Equals(JavaCustomSentinel, StringComparison.OrdinalIgnoreCase))
                    return JavaRuntimeService.ModeCustom;
                return JavaRuntimeService.ModeFromConfig(raw);
            }
            catch
            {
                return JavaRuntimeService.ModeAutomatic;
            }
        }
        set
        {
            var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not (JavaRuntimeService.ModeAutomatic or JavaRuntimeService.ModeSystem or JavaRuntimeService.ModeCustom))
                mode = JavaRuntimeService.ModeAutomatic;

            if (string.Equals(JavaMode, mode, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var current = (_config.Current.JavaPath ?? string.Empty).Trim();
                if (JavaRuntimeService.ModeFromConfig(current) == JavaRuntimeService.ModeCustom &&
                    !current.Equals(JavaCustomSentinel, StringComparison.OrdinalIgnoreCase))
                {
                    _lastCustomJavaPath = current;
                }

                _config.Current.JavaPath = mode switch
                {
                    JavaRuntimeService.ModeSystem => JavaRuntimeService.SystemSentinel,
                    JavaRuntimeService.ModeCustom => string.IsNullOrWhiteSpace(_lastCustomJavaPath) ? JavaCustomSentinel : _lastCustomJavaPath,
                    _ => null
                };
                ScheduleConfigSave();
            }
            catch { }

            _resolvedJavaPath = null;
            JavaStatusText = mode switch
            {
                JavaRuntimeService.ModeSystem => "Будет использована Java 21, установленная в системе.",
                JavaRuntimeService.ModeCustom => "Выберите Java 21 (64-bit).",
                _ => "LegendBorn сам выберет или установит Java 21."
            };
            Raise(nameof(JavaMode));
            Raise(nameof(JavaCustomPath));
            Raise(nameof(JavaCustomPathEnabled));
            Raise(nameof(JavaModeDescription));
            _checkJavaCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool JavaCustomPathEnabled => JavaMode == JavaRuntimeService.ModeCustom;

    public string JavaCustomPath
    {
        get
        {
            try
            {
                var value = (_config.Current.JavaPath ?? string.Empty).Trim();
                if (JavaMode != JavaRuntimeService.ModeCustom || value.Equals(JavaCustomSentinel, StringComparison.OrdinalIgnoreCase))
                    return _lastCustomJavaPath;
                return value;
            }
            catch { return _lastCustomJavaPath; }
        }
        set
        {
            var path = (value ?? string.Empty).Trim().Trim('"');
            if (string.Equals(_lastCustomJavaPath, path, StringComparison.Ordinal))
                return;

            _lastCustomJavaPath = path;
            if (JavaMode == JavaRuntimeService.ModeCustom)
            {
                try
                {
                    _config.Current.JavaPath = string.IsNullOrWhiteSpace(path) ? JavaCustomSentinel : path;
                    ScheduleConfigSave();
                }
                catch { }
            }

            _resolvedJavaPath = null;
            Raise(nameof(JavaCustomPath));
            _checkJavaCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool IsJavaBusy => _isJavaBusy;
    public string JavaStatusText
    {
        get => _javaStatusText;
        private set => Set(ref _javaStatusText, value);
    }

    public string JavaModeDescription => JavaMode switch
    {
        JavaRuntimeService.ModeSystem => "Использовать Java, уже установленную на компьютере.",
        JavaRuntimeService.ModeCustom => "Использовать выбранную вами Java.",
        _ => "Рекомендуется. Лаунчер сам найдёт Java 21 или установит её."
    };

    public AsyncRelayCommand CheckJavaCommand => _checkJavaCommand ??= new AsyncRelayCommand(
        CheckJavaAsync,
        () => !_isClosing && !_isJavaBusy && (JavaMode != JavaRuntimeService.ModeCustom || !string.IsNullOrWhiteSpace(JavaCustomPath)));

    private async Task CheckJavaAsync()
    {
        SetJavaBusy(true);
        try
        {
            JavaStatusText = JavaMode == JavaRuntimeService.ModeAutomatic
                ? "Проверяю Java…"
                : "Проверяю выбранную Java…";

            var info = await _javaRuntime.ResolveAsync(
                _config.Current,
                _gameDir,
                installIfMissing: JavaMode == JavaRuntimeService.ModeAutomatic,
                downloadProgress: p => PostToUi(() => JavaStatusText = $"Устанавливаю Java… {p}%"),
                _lifetimeCts.Token).ConfigureAwait(false);

            ActivateJavaForLauncher(info.JavaExe);
            _resolvedJavaPath = info.JavaExe;
            PostToUi(() => JavaStatusText = $"Готово: {info.DisplayName}");
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _resolvedJavaPath = null;
            AppendLog("Java: " + ex.Message);
            PostToUi(() => JavaStatusText = ex.Message);
        }
        finally
        {
            SetJavaBusy(false);
        }
    }

    private async Task<string> EnsureJavaForLaunchAsync()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedJavaPath))
        {
            var cached = await JavaRuntimeService.ProbeAsync(_resolvedJavaPath, "Текущая", false, _lifetimeCts.Token).ConfigureAwait(false);
            if (cached is { Major: >= JavaRuntimeService.RequiredMajor, Is64Bit: true })
            {
                ActivateJavaForLauncher(cached.JavaExe);
                return cached.JavaExe;
            }
            _resolvedJavaPath = null;
        }

        PostToUi(() => JavaStatusText = "Проверяю Java…");
        var info = await _javaRuntime.ResolveAsync(
            _config.Current,
            _gameDir,
            installIfMissing: JavaMode == JavaRuntimeService.ModeAutomatic,
            downloadProgress: p => PostToUi(() =>
            {
                JavaStatusText = $"Устанавливаю Java… {p}%";
                StatusText = $"Устанавливаю Java… {p}%";
            }),
            _lifetimeCts.Token).ConfigureAwait(false);

        ActivateJavaForLauncher(info.JavaExe);
        _resolvedJavaPath = info.JavaExe;
        PostToUi(() => JavaStatusText = $"Готово: {info.DisplayName}");
        return info.JavaExe;
    }

    private static void ActivateJavaForLauncher(string javaExe)
    {
        try
        {
            var bin = Path.GetDirectoryName(javaExe);
            var home = string.IsNullOrWhiteSpace(bin) ? null : Directory.GetParent(bin)?.FullName;
            if (!string.IsNullOrWhiteSpace(home))
                Environment.SetEnvironmentVariable("JAVA_HOME", home, EnvironmentVariableTarget.Process);
        }
        catch { }
    }

    private void SetJavaBusy(bool value)
    {
        PostToUi(() =>
        {
            _isJavaBusy = value;
            Raise(nameof(IsJavaBusy));
            _checkJavaCommand?.RaiseCanExecuteChanged();
        });
    }
}

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
            catch { return JavaRuntimeService.ModeAutomatic; }
        }
        set
        {
            var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (mode is not (JavaRuntimeService.ModeAutomatic or JavaRuntimeService.ModeSystem or JavaRuntimeService.ModeCustom))
                mode = JavaRuntimeService.ModeAutomatic;
            if (string.Equals(JavaMode, mode, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var current = (_config.Current.JavaPath ?? string.Empty).Trim();
                if (JavaRuntimeService.ModeFromConfig(current) == JavaRuntimeService.ModeCustom &&
                    !current.Equals(JavaCustomSentinel, StringComparison.OrdinalIgnoreCase))
                    _lastCustomJavaPath = current;

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
            RaiseJavaPresentation();
        }
    }

    public bool UseAutomaticJava
    {
        get => JavaMode == JavaRuntimeService.ModeAutomatic;
        set { if (value) JavaMode = JavaRuntimeService.ModeAutomatic; }
    }

    public bool UseSystemJava
    {
        get => JavaMode == JavaRuntimeService.ModeSystem;
        set { if (value) JavaMode = JavaRuntimeService.ModeSystem; }
    }

    public bool UseCustomJava
    {
        get => JavaMode == JavaRuntimeService.ModeCustom;
        set { if (value) JavaMode = JavaRuntimeService.ModeCustom; }
    }

    public bool JavaCustomPathEnabled => UseCustomJava;

    public string JavaCustomPath
    {
        get
        {
            try
            {
                var value = (_config.Current.JavaPath ?? string.Empty).Trim();
                if (!UseCustomJava || value.Equals(JavaCustomSentinel, StringComparison.OrdinalIgnoreCase))
                    return _lastCustomJavaPath;
                return value;
            }
            catch { return _lastCustomJavaPath; }
        }
        set
        {
            var path = (value ?? string.Empty).Trim().Trim('"');
            if (string.Equals(_lastCustomJavaPath, path, StringComparison.Ordinal)) return;
            _lastCustomJavaPath = path;
            if (UseCustomJava)
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

    public AsyncRelayCommand CheckJavaCommand => _checkJavaCommand ??= new AsyncRelayCommand(
        CheckJavaAsync,
        () => !_isClosing && !_isJavaBusy && (!UseCustomJava || !string.IsNullOrWhiteSpace(JavaCustomPath)));

    private async Task CheckJavaAsync()
    {
        SetJavaBusy(true);
        try
        {
            JavaStatusText = "Проверяю Java…";
            var info = await _javaRuntime.ResolveAsync(
                _config.Current,
                _gameDir,
                installIfMissing: UseAutomaticJava,
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
        finally { SetJavaBusy(false); }
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
            installIfMissing: UseAutomaticJava,
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

    private void RaiseJavaPresentation()
    {
        Raise(nameof(JavaMode));
        Raise(nameof(UseAutomaticJava));
        Raise(nameof(UseSystemJava));
        Raise(nameof(UseCustomJava));
        Raise(nameof(JavaCustomPath));
        Raise(nameof(JavaCustomPathEnabled));
        _checkJavaCommand?.RaiseCanExecuteChanged();
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

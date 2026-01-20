using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private string _lastAutoServerIp = "";
    private string _previousSelectedServerAddress = "";

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            await LoadServersAsync(ct);

            if (_config.Current.AutoLogin)
                await TryAutoLoginAsync(ct);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Инициализация: отменено.");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void OnSelectedServerChanged(ServerEntry? value)
    {
        if (_isClosing) return;

        if (value is null)
        {
            RaisePackPresentation();
            RefreshCanStates();
            return;
        }

        try
        {
            var current = (ServerIp ?? "").Trim();
            var addr = (value.Address ?? "").Trim();

            var shouldAuto =
                string.IsNullOrWhiteSpace(current) ||
                current.Equals(DefaultServerIp, StringComparison.OrdinalIgnoreCase) ||
                current.Equals(_lastAutoServerIp, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(_previousSelectedServerAddress) &&
                 current.Equals(_previousSelectedServerAddress, StringComparison.OrdinalIgnoreCase));

            if (shouldAuto && !string.IsNullOrWhiteSpace(addr))
            {
                ServerIp = addr;
                _lastAutoServerIp = addr;
            }

            _previousSelectedServerAddress = addr;
        }
        catch { }

        try
        {
            var label = MakeAutoVersionLabel(value);
            SetVersionsUi(label);
        }
        catch { }

        try
        {
            _config.Current.LastServerId = value.Id;
            ScheduleConfigSave();
        }
        catch { }

        RaisePackPresentation();
        RefreshCanStates();
    }

    private async Task LoadServersAsync(CancellationToken ct)
    {
        if (_isClosing) return;

        try
        {
            AppendLog("Серверы: загрузка списка...");

            var list = await _servers.GetServersOrDefaultAsync(
                mirrors: ServerListService.DefaultServersMirrors,
                ct: ct);

            var savedId = "";
            try { savedId = (_config.Current.LastServerId ?? "").Trim(); } catch { }

            InvokeOnUi(() =>
            {
                Servers.Clear();

                foreach (var s in list)
                {
                    var loaderType = (s.Loader?.Type ?? s.LoaderName ?? "vanilla").Trim().ToLowerInvariant();
                    var loaderVer = (s.Loader?.Version ?? s.LoaderVersion ?? "").Trim();
                    var installerUrl = (s.Loader?.InstallerUrl ?? "").Trim();

                    Servers.Add(new ServerEntry
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Address = s.Address,
                        MinecraftVersion = s.MinecraftVersion,

                        LoaderName = loaderType,
                        LoaderVersion = loaderVer,
                        LoaderInstallerUrl = installerUrl,

                        PackBaseUrl = EnsureSlash(s.PackBaseUrl),
                        PackMirrors = s.PackMirrors ?? Array.Empty<string>(),
                        SyncPack = s.SyncPack
                    });
                }

                _suppressSelectedServerSideEffects = true;
                try
                {
                    SelectedServer =
                        Servers.FirstOrDefault(x => x.Id.Equals(savedId, StringComparison.OrdinalIgnoreCase)) ??
                        Servers.FirstOrDefault();
                }
                finally
                {
                    _suppressSelectedServerSideEffects = false;
                }

                OnSelectedServerChanged(SelectedServer);
            });

            AppendLog($"Серверы: загружено {Servers.Count} шт.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Серверы: отменено.");
        }
        catch (Exception ex)
        {
            AppendLog("Серверы: ошибка загрузки.");
            AppendLog(ex.Message);
        }
        finally
        {
            RefreshCanStates();
        }
    }
}

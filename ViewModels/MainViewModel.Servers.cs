// File: ViewModels/MainViewModel.Servers.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Services;

namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            // The vanilla multiplayer list is launcher-owned. Keep exactly one canonical LegendBorn
            // entry even if a clean install or Minecraft itself rewrites/deletes servers.dat later.
            MinecraftServerListPolicy.StartEnforcement(_gameDir, AppendLog);

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
            // Build/loader/pack metadata still comes from the signed/authoritative catalog, but the
            // actual public game endpoint is deliberately pinned in the launcher. This prevents a
            // stale catalog/cache or old LastServerIp from breaking Quick Play after a server move.
            var addr = MinecraftServerListPolicy.ResolveLaunchAddress(value.Address, AppendLog);
            if (!string.Equals(ServerIp, addr, StringComparison.OrdinalIgnoreCase))
                ServerIp = addr;
        }
        catch { /* ignore */ }

        try
        {
            var label = MakeAutoVersionLabel(value);
            SetVersionsUi(label);
        }
        catch { /* ignore */ }

        try
        {
            _config.Current.LastServerId = value.Id;
            _config.Current.LastServerIp = MinecraftServerListPolicy.CanonicalServerAddress;
            ScheduleConfigSave();
        }
        catch { /* ignore */ }

        RaisePackPresentation();
        RefreshCanStates();
    }

    private async Task LoadServersAsync(CancellationToken ct)
    {
        if (_isClosing) return;

        try
        {
            AppendLog("Серверы: загрузка актуального каталога...");

            var list = await ServerCatalogService.GetServersAsync(
                log: message => AppendLog(message),
                ct: ct);

            var savedId = "";
            try { savedId = (_config.Current.LastServerId ?? "").Trim(); } catch { /* ignore */ }

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
                        Address = MinecraftServerListPolicy.ResolveLaunchAddress(s.Address),
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

            AppendLog($"Серверы: загружено {Servers.Count} шт.; игровой адрес {MinecraftServerListPolicy.CanonicalServerAddress}.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Серверы: отменено.");
        }
        catch (Exception ex)
        {
            // Fail closed. Missing build metadata is worse than launching an unknown distribution.
            InvokeOnUi(() =>
            {
                Servers.Clear();
                SelectedServer = null;
            });

            AppendLog("Серверы: не удалось получить актуальный каталог; запуск отключён.");
            AppendLog(ex.Message);
            StatusText = "Не удалось получить актуальные данные игрового сервера.";
        }
        finally
        {
            RefreshCanStates();
        }
    }
}

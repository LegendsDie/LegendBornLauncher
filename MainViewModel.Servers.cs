using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegendBorn.Services;

namespace LegendBorn;

public sealed partial class MainViewModel
{
    private async Task InitializeAsync()
    {
        try
        {
            await LoadServersAsync();

            if (SelectedServer is not null)
                SetVersionsUi(MakeAutoVersionLabel(SelectedServer));

            await TryAutoLoginAsync();
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
        }
    }

    private void OnSelectedServerChanged(ServerEntry? value)
    {
        if (value is null)
        {
            RaisePackPresentation();
            return;
        }

        // Apply UI state
        ServerIp = value.Address;

        var label = MakeAutoVersionLabel(value);
        SetVersionsUi(label);

        // Persist selection
        TrySaveSetting("SelectedServerId", value.Id);
        SaveSettingsSafe();

        // Update pack labels
        RaisePackPresentation();
    }

    private async Task LoadServersAsync()
    {
        try
        {
            AppendLog("Серверы: загрузка списка...");

            var list = await _servers.GetServersOrDefaultAsync(
                mirrors: ServerListService.DefaultServersMirrors,
                ct: CancellationToken.None);

            int count = 0;

            PostToUi(() =>
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

                var savedId = TryLoadStringSetting("SelectedServerId", null);

                _suppressSelectedServerSideEffects = true;
                try
                {
                    SelectedServer =
                        Servers.FirstOrDefault(x => x.Id.Equals(savedId ?? "", StringComparison.OrdinalIgnoreCase)) ??
                        Servers.FirstOrDefault();
                }
                finally
                {
                    _suppressSelectedServerSideEffects = false;
                }

                // now run side-effects once
                OnSelectedServerChanged(SelectedServer);

                count = Servers.Count;
            });

            AppendLog($"Серверы: загружено {count} шт.");
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

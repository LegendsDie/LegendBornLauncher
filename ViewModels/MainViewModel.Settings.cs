namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Lets the launcher keep the persisted RAM choice in AUTO mode while exposing the
    /// currently calculated effective value through RamMb for the existing launch pipeline.
    /// </summary>
    public bool UseAutomaticRam
    {
        get
        {
            try { return _config.Current.RamMb <= 0; }
            catch { return false; }
        }
        set
        {
            try
            {
                var current = _config.Current.RamMb <= 0;
                if (current == value) return;

                if (value)
                {
                    _config.Current.RamMb = 0;

                    var effective = _recommendedRamMb > 0
                        ? _recommendedRamMb
                        : RamMinMb;
                    effective = NormalizeRamMb(effective);

                    if (_ramMb != effective)
                    {
                        _ramMb = effective;
                        EnsureRamOptionExists(_ramMb);
                        Raise(nameof(RamMb));
                        Raise(nameof(RamMbText));
                    }
                }
                else
                {
                    _config.Current.RamMb = _ramMb;
                }

                ScheduleConfigSave();
                Raise(nameof(UseAutomaticRam));
                Raise(nameof(ManualRamEnabled));
            }
            catch
            {
            }
        }
    }

    public bool ManualRamEnabled => !UseAutomaticRam;

    /// <summary>Read-only instance path shown in Settings; folder ownership is still LauncherPaths/config driven.</summary>
    public string GameDirectoryPath => _gameDir;

    public bool AutoLogin
    {
        get
        {
            try { return _config.Current.AutoLogin; }
            catch { return true; }
        }
        set
        {
            try
            {
                if (_config.Current.AutoLogin == value) return;
                _config.Current.AutoLogin = value;
                ScheduleConfigSave();
                Raise(nameof(AutoLogin));
            }
            catch
            {
            }
        }
    }

    public bool AutoConnect
    {
        get
        {
            try { return _config.Current.AutoConnect; }
            catch { return true; }
        }
        set
        {
            try
            {
                if (_config.Current.AutoConnect == value) return;
                _config.Current.AutoConnect = value;
                ScheduleConfigSave();
                Raise(nameof(AutoConnect));
            }
            catch
            {
            }
        }
    }
}

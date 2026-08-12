namespace LegendBorn.ViewModels;

public sealed partial class MainViewModel
{
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

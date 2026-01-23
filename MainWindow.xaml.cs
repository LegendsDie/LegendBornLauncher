// File: MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using LegendBorn.Services;
using LegendBorn.ViewModels;

namespace LegendBorn;

public partial class MainWindow : Window
{
    private const int NewsTabIndex = 4;
    private const int CopyLogsMaxLines = 120;

    // В XAML/подписках у тебя встречается ru.legendborn.ru, но он иногда недоступен.
    // Для открытия сайта и подтягивания новостей используем основной домен, а ru — как резерв.
    private const string SiteUrlPrimary = "https://legendborn.ru/";
    private const string SiteUrlFallback = "https://ru.legendborn.ru/";

    private bool _updatesChecked;
    private bool _isClosing;

    private readonly MainViewModel _vm;

    // ===== responsive sizing =====
    private bool _responsiveApplied;

    // ===== maximize/restore =====
    private Rect _restoreBounds;
    private bool _hasRestoreBounds;

    // ===== prefs =====
    private enum LauncherGameUiMode { Hide, Minimize, None }
    private LauncherGameUiMode _gameUiMode = LauncherGameUiMode.Hide; // default
    private bool _settingModeGuard;

    private bool _wasGameRunning;
    private bool _uiChangedForGame;
    private WindowState _preGameWindowState;
    private bool _preGameWasVisible;

    // ===== logs autoscroll =====
    private bool _logAutoScroll = true;
    private ScrollChangedEventHandler? _logScrollHandler;

    // ===== news =====
    private CancellationTokenSource? _newsCts;
    private static readonly HttpClient NewsHttp = CreateNewsHttp();

    private static readonly string NewsCacheFilePath =
        Path.Combine(LauncherPaths.CacheDir, "news_cache.json");

    // UI-level news model (RootWindow fallback)
    public sealed class NewsItem
    {
        public string Title { get; init; } = "";
        public string Date { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Url { get; init; } = "";
    }

    // XAML binds via ElementName=RootWindow (fallback); PriorityBinding сперва пытается VM
    public ObservableCollection<NewsItem> ServerNewsTop2 { get; } = new();
    public ObservableCollection<NewsItem> ProjectNews { get; } = new();

    // prefs location (0.2.6+): %AppData%\LegendBorn\launcher.prefs.json
    private static readonly string PrefsPath = Path.Combine(LauncherPaths.AppDir, "launcher.prefs.json");

    // migration path (older builds used "LegendCraft")
    private static readonly string OldPrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendCraft",
        "launcher_prefs.json");

    public bool GameUiModeHide
    {
        get => (bool)GetValue(GameUiModeHideProperty);
        set => SetValue(GameUiModeHideProperty, value);
    }

    public static readonly DependencyProperty GameUiModeHideProperty =
        DependencyProperty.Register(nameof(GameUiModeHide), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    public bool GameUiModeMinimize
    {
        get => (bool)GetValue(GameUiModeMinimizeProperty);
        set => SetValue(GameUiModeMinimizeProperty, value);
    }

    public static readonly DependencyProperty GameUiModeMinimizeProperty =
        DependencyProperty.Register(nameof(GameUiModeMinimize), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    public bool GameUiModeNone
    {
        get => (bool)GetValue(GameUiModeNoneProperty);
        set => SetValue(GameUiModeNoneProperty, value);
    }

    public static readonly DependencyProperty GameUiModeNoneProperty =
        DependencyProperty.Register(nameof(GameUiModeNone), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false, OnGameUiModeFlagChanged));

    private static void OnGameUiModeFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (MainWindow)d;
        if (w._settingModeGuard) return;
        if (e.NewValue is not bool b || !b) return;

        if (ReferenceEquals(e.Property, GameUiModeHideProperty))
            w.SetUiMode(LauncherGameUiMode.Hide);
        else if (ReferenceEquals(e.Property, GameUiModeMinimizeProperty))
            w.SetUiMode(LauncherGameUiMode.Minimize);
        else if (ReferenceEquals(e.Property, GameUiModeNoneProperty))
            w.SetUiMode(LauncherGameUiMode.None);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Сразу показываем кеш/дефолт (чтобы UI не был пустым),
        // затем в фоне подтягиваем новости с сайта.
        TryLoadNewsCacheOrSeed();

        // prefs can be loaded before VM (they target Window DP)
        LoadPrefs();
        ApplyModeToBindings();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.PropertyChanged += VmOnPropertyChanged;

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        // Запоминаем restore bounds только когда окно в Normal.
        StateChanged += (_, __) => OnWindowBoundsPossiblyChanged();
        LocationChanged += (_, __) => OnWindowBoundsPossiblyChanged();
        SizeChanged += (_, __) => OnWindowBoundsPossiblyChanged();
    }

    private void OnWindowBoundsPossiblyChanged()
    {
        try
        {
            if (_isClosing) return;
            if (WindowState == WindowState.Normal)
                UpdateRestoreBoundsFromWindow();
        }
        catch { }
    }

    // ===================== Window lifecycle =====================
    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        TryUnhookLogsUi();
        CancelNewsLoading();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;

        try { _vm.PropertyChanged -= VmOnPropertyChanged; } catch { }

        TryUnhookLogsUi();
        CancelNewsLoading();

        try { SavePrefs(); } catch { }
        try { _vm.MarkClosing(); } catch { }
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveWindowSizeOnce();
        HookLogsUi();

        // Подтянуть новости (сайт) после появления окна
        _ = RefreshNewsFromSiteSafeAsync();

        if (_updatesChecked) return;
        _updatesChecked = true;

        _ = RunUpdateCheckSafeAsync();
    }

    // ===================== News =====================
    private static HttpClient CreateNewsHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = WebRequest.DefaultWebProxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan // таймауты per-request через CTS
        };

        try
        {
            var ua = LauncherIdentity.UserAgent;
            if (!string.IsNullOrWhiteSpace(ua))
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }
        catch
        {
            try
            {
                http.DefaultRequestHeaders.UserAgent.Clear();
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"LegendBornLauncher/{LauncherIdentity.InformationalVersion}");
            }
            catch { }
        }

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

        return http;
    }

    private void CancelNewsLoading()
    {
        try { _newsCts?.Cancel(); } catch { }
        try { _newsCts?.Dispose(); } catch { }
        _newsCts = null;
    }

    private void TryLoadNewsCacheOrSeed()
    {
        if (TryLoadNewsCache(out var cachedServer, out var cachedProject))
        {
            ReplaceNewsCollections(cachedServer, cachedProject);
            return;
        }

        SeedNewsFallback();
    }

    private void SeedNewsFallback()
    {
        var now = DateTime.Now;

        ServerNewsTop2.Clear();
        ServerNewsTop2.Add(new NewsItem
        {
            Title = "Технические работы",
            Date = now.ToString("dd.MM", CultureInfo.InvariantCulture),
            Summary = "Сегодня возможны краткие перезапуски сервера. Спасибо за понимание.",
            Url = SiteUrlPrimary
        });
        ServerNewsTop2.Add(new NewsItem
        {
            Title = "Обновление сборки",
            Date = now.AddDays(-1).ToString("dd.MM", CultureInfo.InvariantCulture),
            Summary = "Исправления стабильности и подготовка к новым механикам.",
            Url = SiteUrlPrimary
        });

        ProjectNews.Clear();
        ProjectNews.Add(new NewsItem
        {
            Title = "LegendBorn: Дорожная карта",
            Date = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            Summary = "Публикуем ближайшие цели и приоритеты разработки.",
            Url = SiteUrlPrimary
        });
        ProjectNews.Add(new NewsItem
        {
            Title = "Launcher: улучшения интерфейса",
            Date = now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            Summary = "Новый профиль, красивее новости, стабильнее загрузка данных.",
            Url = SiteUrlPrimary
        });
    }

    private async Task RefreshNewsFromSiteSafeAsync()
    {
        if (_isClosing) return;

        CancelNewsLoading();
        _newsCts = new CancellationTokenSource();
        var ct = _newsCts.Token;

        try
        {
            // Дадим окну быстро отрисоваться, не занимая UI-поток без нужды
            await Task.Delay(150, ct).ConfigureAwait(false);

            var (server, project) = await FetchNewsSmartAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || _isClosing) return;

            if (server.Count == 0 && project.Count == 0)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isClosing) return;
                ReplaceNewsCollections(server, project);
            });

            SaveNewsCacheQuiet(server, project);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // если упало — оставляем то, что уже показано (кеш/seed)
        }
    }

    private void ReplaceNewsCollections(IReadOnlyList<NewsItem> server, IReadOnlyList<NewsItem> project)
    {
        ServerNewsTop2.Clear();
        foreach (var n in server.Take(2))
            ServerNewsTop2.Add(n);

        ProjectNews.Clear();
        foreach (var n in project)
            ProjectNews.Add(n);
    }

    private static void SaveNewsCacheQuiet(IReadOnlyList<NewsItem> server, IReadOnlyList<NewsItem> project)
    {
        try
        {
            LauncherPaths.EnsureDir(LauncherPaths.CacheDir);

            var dto = new NewsCacheDto
            {
                FetchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Server = server.ToList(),
                Project = project.ToList()
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            });

            var tmp = NewsCacheFilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ReplaceOrMoveAtomic(tmp, NewsCacheFilePath);
            TryDeleteQuiet(tmp);
        }
        catch { }
    }

    private static bool TryLoadNewsCache(out List<NewsItem> server, out List<NewsItem> project)
    {
        server = new List<NewsItem>();
        project = new List<NewsItem>();

        try
        {
            if (!File.Exists(NewsCacheFilePath))
                return false;

            var json = File.ReadAllText(NewsCacheFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var dto = JsonSerializer.Deserialize<NewsCacheDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            if (dto == null)
                return false;

            if (dto.Project != null) project = dto.Project.Where(IsValidNews).ToList();
            if (dto.Server != null) server = dto.Server.Where(IsValidNews).ToList();

            if (server.Count == 0 && project.Count > 0)
                server = project.Take(2).ToList();

            if (project.Count == 0 && server.Count > 0)
                project = server.ToList();

            return server.Count > 0 || project.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class NewsCacheDto
    {
        public long FetchedAtUnix { get; set; }
        public List<NewsItem>? Server { get; set; }
        public List<NewsItem>? Project { get; set; }
    }

    private static bool IsValidNews(NewsItem n)
        => n != null
           && !string.IsNullOrWhiteSpace(n.Title)
           && !string.IsNullOrWhiteSpace(n.Url);

    private static async Task<(List<NewsItem> Server, List<NewsItem> Project)> FetchNewsSmartAsync(CancellationToken ct)
    {
        var bases = new[]
        {
            SiteUrlPrimary.TrimEnd('/'),
            SiteUrlFallback.TrimEnd('/')
        };

        var paths = new[]
        {
            // JSON (желательно)
            "/api/launcher/news",
            "/api/launcher/news.json",
            "/api/news",
            "/api/news.json",
            "/launcher/news.json",
            "/launcher/newsfeed.json",
            "/launcher/news_feed.json",
            "/news.json",

            // RSS/Atom (если есть)
            "/feed",
            "/rss",
            "/rss.xml",
            "/feed.xml",
            "/news/feed",
            "/blog/feed",
            "/blog/rss",
        };

        foreach (var b in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in paths)
            {
                var url = b + p;
                var res = await TryFetchAndParseNewsAsync(url, ct, timeout: TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                if (res.Server.Count > 0 || res.Project.Count > 0)
                    return res;
            }
        }

        return (new List<NewsItem>(), new List<NewsItem>());
    }

    private static async Task<(List<NewsItem> Server, List<NewsItem> Project)> TryFetchAndParseNewsAsync(
        string url,
        CancellationToken ct,
        TimeSpan timeout)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var resp = await NewsHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (new List<NewsItem>(), new List<NewsItem>());

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var body = await ReadUtf8LimitedAsync(resp, 512 * 1024, reqCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return (new List<NewsItem>(), new List<NewsItem>());

            var trimmed = body.TrimStart();

            var looksJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                            || trimmed.StartsWith("{", StringComparison.Ordinal)
                            || trimmed.StartsWith("[", StringComparison.Ordinal);

            var looksXml = contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                           || trimmed.StartsWith("<", StringComparison.Ordinal);

            if (looksJson)
                return ParseNewsFromJson(body);

            if (looksXml)
                return ParseNewsFromXml(body);

            return (new List<NewsItem>(), new List<NewsItem>());
        }
        catch
        {
            return (new List<NewsItem>(), new List<NewsItem>());
        }
    }

    private static async Task<string> ReadUtf8LimitedAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var total = 0;

        using var ms = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
            if (total > maxBytes)
                return "";

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static (List<NewsItem> Server, List<NewsItem> Project) ParseNewsFromJson(string json)
    {
        var server = new List<NewsItem>();
        var project = new List<NewsItem>();

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;

            // Вариант A: { server:[...], project:[...] }
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadArray(root, "server", out var serverArr))
                    server.AddRange(ReadItemsArray(serverArr));

                if (TryReadArray(root, "project", out var projectArr))
                    project.AddRange(ReadItemsArray(projectArr));

                // Вариант B: { items:[...] }
                if (server.Count == 0 && project.Count == 0 && TryReadArray(root, "items", out var itemsArr))
                    project.AddRange(ReadItemsArray(itemsArr));

                // Вариант C: { news/posts/data/entries:[...] }
                if (server.Count == 0 && project.Count == 0)
                {
                    foreach (var key in new[] { "news", "posts", "data", "entries" })
                    {
                        if (TryReadArray(root, key, out var arr))
                        {
                            project.AddRange(ReadItemsArray(arr));
                            break;
                        }
                    }
                }
            }

            // Вариант D: корень массив
            if (root.ValueKind == JsonValueKind.Array && project.Count == 0)
                project.AddRange(ReadItemsArray(root));

            project = DedupSort(project);
            server = DedupSort(server);

            if (server.Count == 0 && project.Count > 0)
                server = project.Take(2).ToList();

            if (project.Count == 0 && server.Count > 0)
                project = server.ToList();
        }
        catch
        {
            // ignore
        }

        return (server, project);

        static bool TryReadArray(JsonElement obj, string name, out JsonElement arr)
        {
            arr = default;
            if (obj.ValueKind != JsonValueKind.Object) return false;
            if (!obj.TryGetProperty(name, out var p)) return false;
            if (p.ValueKind != JsonValueKind.Array) return false;
            arr = p;
            return true;
        }

        static List<NewsItem> ReadItemsArray(JsonElement arr)
        {
            var list = new List<NewsItem>();

            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object)
                    continue;

                var title = GetString(it, "title");
                if (string.IsNullOrWhiteSpace(title))
                    title = GetString(it, "name");

                var url = GetString(it, "url");
                if (string.IsNullOrWhiteSpace(url))
                    url = GetString(it, "link");

                var summary = GetString(it, "summary");
                if (string.IsNullOrWhiteSpace(summary))
                    summary = GetString(it, "excerpt");
                if (string.IsNullOrWhiteSpace(summary))
                    summary = GetString(it, "description");

                var dateStr = GetString(it, "date");
                if (string.IsNullOrWhiteSpace(dateStr))
                    dateStr = GetString(it, "publishedAt");
                if (string.IsNullOrWhiteSpace(dateStr))
                    dateStr = GetString(it, "pubDate");

                var dateUnix = GetInt64(it, "dateUnix");
                if (dateUnix <= 0)
                    dateUnix = GetInt64(it, "publishedAtUnix");
                if (dateUnix <= 0)
                    dateUnix = GetInt64(it, "createdAtUnix");

                var date = FormatDateSmart(dateStr, dateUnix);

                title = (title ?? "").Trim();
                url = (url ?? "").Trim();
                summary = WebUtility.HtmlDecode((summary ?? "").Trim());

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
                    continue;

                url = NormalizeUrl(url);

                list.Add(new NewsItem
                {
                    Title = title,
                    Date = date,
                    Summary = string.IsNullOrWhiteSpace(summary) ? "Открыть новость на сайте." : summary,
                    Url = url
                });
            }

            return list;

            static string GetString(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? (p.GetString() ?? "")
                    : "";

            static long GetInt64(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)
                    ? v
                    : 0;
        }

        static List<NewsItem> DedupSort(List<NewsItem> list)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleaned = new List<(NewsItem Item, DateTimeOffset SortKey)>();

            foreach (var n in list)
            {
                if (!IsValidNews(n)) continue;

                var key = (n.Url ?? "").Trim();
                if (!seen.Add(key)) continue;

                cleaned.Add((n, ParseDateForSort(n.Date)));
            }

            return cleaned
                .OrderByDescending(x => x.SortKey)
                .Select(x => x.Item)
                .ToList();

            static DateTimeOffset ParseDateForSort(string s)
            {
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
                    return dto;

                if (DateTime.TryParseExact(s, new[] { "dd.MM.yyyy", "dd.MM" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var dt))
                {
                    if (dt.Year == 1)
                        dt = new DateTime(DateTime.Now.Year, dt.Month, dt.Day);
                    return new DateTimeOffset(dt);
                }

                return DateTimeOffset.MinValue;
            }
        }
    }

    private static (List<NewsItem> Server, List<NewsItem> Project) ParseNewsFromXml(string xml)
    {
        var project = new List<NewsItem>();

        try
        {
            var doc = XDocument.Parse(xml);

            // RSS: <rss><channel><item>...
            var items = doc.Descendants().Where(x => x.Name.LocalName == "item").ToList();
            if (items.Count == 0)
            {
                // Atom: <feed><entry>...
                items = doc.Descendants().Where(x => x.Name.LocalName == "entry").ToList();
            }

            foreach (var it in items)
            {
                var title = it.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value?.Trim() ?? "";
                var link = "";

                // RSS: <link>url</link>
                var linkEl = it.Descendants().FirstOrDefault(x => x.Name.LocalName == "link");
                if (linkEl != null)
                {
                    // Atom: <link href="..."/>
                    var href = linkEl.Attribute("href")?.Value;
                    link = !string.IsNullOrWhiteSpace(href) ? href.Trim() : (linkEl.Value?.Trim() ?? "");
                }

                var desc = it.Descendants().FirstOrDefault(x => x.Name.LocalName == "description")?.Value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(desc))
                    desc = it.Descendants().FirstOrDefault(x => x.Name.LocalName == "summary")?.Value?.Trim() ?? "";

                var pub = it.Descendants()
                              .FirstOrDefault(x => x.Name.LocalName is "pubDate" or "published" or "updated")
                              ?.Value?.Trim() ?? "";

                var date = FormatDateSmart(pub, 0);

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                    continue;

                link = NormalizeUrl(link);

                project.Add(new NewsItem
                {
                    Title = WebUtility.HtmlDecode(title),
                    Date = date,
                    Summary = string.IsNullOrWhiteSpace(desc) ? "Открыть новость на сайте." : StripHtmlLoose(desc),
                    Url = link
                });
            }
        }
        catch
        {
            // ignore
        }

        project = project
            .Where(IsValidNews)
            .GroupBy(n => n.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var server = project.Take(2).ToList();
        return (server, project);

        static string StripHtmlLoose(string s)
        {
            try
            {
                s = WebUtility.HtmlDecode(s);
                s = s.Replace("\r", " ").Replace("\n", " ");
                while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");

                var sb = new StringBuilder(s.Length);
                var inside = false;

                foreach (var ch in s)
                {
                    if (ch == '<') { inside = true; continue; }
                    if (ch == '>') { inside = false; continue; }
                    if (!inside) sb.Append(ch);
                }

                var res = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(res) ? s.Trim() : res;
            }
            catch
            {
                return (s ?? "").Trim();
            }
        }
    }

    private static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return SiteUrlPrimary;

        // относительные ссылки приводим к primary домену
        if (Uri.TryCreate(url, UriKind.Relative, out var rel))
            return new Uri(new Uri(SiteUrlPrimary), rel).ToString();

        return url;
    }

    private static string FormatDateSmart(string dateStr, long unixSeconds)
    {
        try
        {
            if (unixSeconds > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
                return dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }

            dateStr = (dateStr ?? "").Trim();
            if (string.IsNullOrWhiteSpace(dateStr))
                return DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

            if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

            // RSS pubDate в RFC1123
            if (DateTimeOffset.TryParseExact(dateStr, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                return dto.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

            // dd.MM или dd.MM.yyyy
            if (DateTime.TryParseExact(dateStr, new[] { "dd.MM.yyyy", "dd.MM" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt2))
            {
                if (dt2.Year == 1)
                    dt2 = new DateTime(DateTime.Now.Year, dt2.Month, dt2.Day);
                return dt2.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }

            return dateStr.Length > 12 ? dateStr.Substring(0, 12) : dateStr;
        }
        catch
        {
            return DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        }
    }

    // ===================== Responsive Window Size =====================
    private void ApplyResponsiveWindowSizeOnce()
    {
        if (_responsiveApplied) return;
        _responsiveApplied = true;

        try
        {
            var work = SystemParameters.WorkArea;

            var maxW = Math.Max(800, work.Width - 80);
            var maxH = Math.Max(540, work.Height - 80);

            var presets = new (double w, double h)[]
            {
                (1280, 860),
                (1200, 800),
                (1100, 740),
                (1020, 700),
                (980, 660),
                (920, 600),
            };

            (double w, double h) chosen = (Width, Height);
            foreach (var p in presets)
            {
                if (p.w <= maxW && p.h <= maxH)
                {
                    chosen = p;
                    break;
                }
            }

            Width = Math.Min(chosen.w, maxW);
            Height = Math.Min(chosen.h, maxH);

            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;

            UpdateRestoreBoundsFromWindow();
        }
        catch
        {
            // keep XAML size
        }
    }

    private void UpdateRestoreBoundsFromWindow()
    {
        try
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            _hasRestoreBounds = _restoreBounds.Width > 0 && _restoreBounds.Height > 0;
        }
        catch { }
    }

    private static Rect ClampToWorkArea(Rect r)
    {
        try
        {
            var work = SystemParameters.WorkArea;

            var minW = 600.0;
            var minH = 420.0;

            var w = Math.Max(minW, Math.Min(r.Width, work.Width));
            var h = Math.Max(minH, Math.Min(r.Height, work.Height));

            var left = r.Left;
            var top = r.Top;

            if (left < work.Left) left = work.Left;
            if (top < work.Top) top = work.Top;

            if (left + w > work.Right) left = Math.Max(work.Left, work.Right - w);
            if (top + h > work.Bottom) top = Math.Max(work.Top, work.Bottom - h);

            return new Rect(left, top, w, h);
        }
        catch
        {
            return r;
        }
    }

    // ===================== Logs UI =====================
    private void HookLogsUi()
    {
        try
        {
            if (_isClosing) return;
            if (LogListBox == null) return;

            _logScrollHandler ??= LogScrollViewer_ScrollChanged;
            LogListBox.AddHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);

            if (_vm.LogLines is INotifyCollectionChanged ncc)
                ncc.CollectionChanged += LogLines_CollectionChanged;

            Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: true)));
        }
        catch { }
    }

    private void TryUnhookLogsUi()
    {
        try
        {
            if (_vm.LogLines is INotifyCollectionChanged ncc)
                ncc.CollectionChanged -= LogLines_CollectionChanged;
        }
        catch { }

        try
        {
            if (LogListBox != null && _logScrollHandler != null)
                LogListBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, _logScrollHandler);
        }
        catch { }
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            // Если изменился Extent — это добавились строки (не пользовательский скролл)
            if (e.ExtentHeightChange != 0)
                return;

            var bottom = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
            _logAutoScroll = e.VerticalOffset >= bottom - 1.0;
        }
        catch { }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isClosing) return;
        if (!_logAutoScroll) return;

        Dispatcher.BeginInvoke(new Action(() => ScrollLogsToEnd(force: false)));
    }

    private void ScrollLogsToEnd(bool force)
    {
        try
        {
            if (_isClosing) return;
            if (LogListBox == null) return;

            var count = LogListBox.Items.Count;
            if (count <= 0) return;

            if (!force && !_logAutoScroll)
                return;

            LogListBox.ScrollIntoView(LogListBox.Items[count - 1]);
        }
        catch { }
    }

    // ===================== Game running / mode =====================
    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosing) return;

        if (e.PropertyName != nameof(MainViewModel.CanStop))
            return;

        var running = _vm.CanStop;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing) return;

            if (running && !_wasGameRunning)
                OnGameStarted();

            if (!running && _wasGameRunning)
                OnGameStopped();

            _wasGameRunning = running;
        }));
    }

    private void OnGameStarted()
    {
        if (_gameUiMode == LauncherGameUiMode.None)
            return;

        _preGameWindowState = WindowState;
        _preGameWasVisible = IsVisible;
        _uiChangedForGame = true;

        try
        {
            if (_gameUiMode == LauncherGameUiMode.Hide)
            {
                if (IsVisible) Hide();
            }
            else if (_gameUiMode == LauncherGameUiMode.Minimize)
            {
                if (WindowState != WindowState.Minimized)
                    WindowState = WindowState.Minimized;
            }
        }
        catch { }
    }

    private void OnGameStopped()
    {
        if (!_uiChangedForGame)
            return;

        _uiChangedForGame = false;

        try
        {
            if (_gameUiMode == LauncherGameUiMode.Hide && _preGameWasVisible && !IsVisible)
                Show();

            WindowState = _preGameWindowState == WindowState.Minimized
                ? WindowState.Normal
                : _preGameWindowState;

            if (IsVisible)
            {
                Activate();
                Topmost = true;
                Topmost = false;
            }
        }
        catch { }
    }

    private void SetUiMode(LauncherGameUiMode mode)
    {
        if (_gameUiMode == mode) return;
        _gameUiMode = mode;

        ApplyModeToBindings();
        try { SavePrefs(); } catch { }
    }

    private void ApplyModeToBindings()
    {
        _settingModeGuard = true;
        try
        {
            GameUiModeHide = _gameUiMode == LauncherGameUiMode.Hide;
            GameUiModeMinimize = _gameUiMode == LauncherGameUiMode.Minimize;
            GameUiModeNone = _gameUiMode == LauncherGameUiMode.None;
        }
        finally
        {
            _settingModeGuard = false;
        }
    }

    private sealed class PrefsDto
    {
        public string? GameUiMode { get; set; }
    }

    private void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefsPath) && File.Exists(OldPrefsPath))
            {
                try
                {
                    LauncherPaths.EnsureParentDirForFile(PrefsPath);
                    File.Copy(OldPrefsPath, PrefsPath, overwrite: true);
                }
                catch { }
            }

            if (!File.Exists(PrefsPath))
            {
                _gameUiMode = LauncherGameUiMode.Hide;
                return;
            }

            var json = File.ReadAllText(PrefsPath, Encoding.UTF8);
            var dto = JsonSerializer.Deserialize<PrefsDto>(json);

            var s = (dto?.GameUiMode ?? "").Trim();
            _gameUiMode =
                s.Equals(nameof(LauncherGameUiMode.Minimize), StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.Minimize :
                s.Equals(nameof(LauncherGameUiMode.None), StringComparison.OrdinalIgnoreCase) ? LauncherGameUiMode.None :
                LauncherGameUiMode.Hide;
        }
        catch
        {
            _gameUiMode = LauncherGameUiMode.Hide;
        }
    }

    private void SavePrefs()
    {
        try
        {
            LauncherPaths.EnsureParentDirForFile(PrefsPath);

            var dto = new PrefsDto { GameUiMode = _gameUiMode.ToString() };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });

            var tmp = PrefsPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(PrefsPath))
            {
                var bak = PrefsPath + ".bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                File.Replace(tmp, PrefsPath, bak, ignoreMetadataErrors: true);
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            }
            else
            {
                File.Move(tmp, PrefsPath);
            }

            TryDeleteQuiet(tmp);
        }
        catch { }
    }

    private async Task RunUpdateCheckSafeAsync()
    {
        try
        {
            if (_isClosing) return;
            await UpdateService.CheckAndUpdateAsync(silent: false, showNoUpdates: false).ConfigureAwait(false);
        }
        catch { }
    }

    // ===================== Helpers =====================
    private static void TryOpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static bool IsClickOnInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            if (d is TextBoxBase) return true;
            if (d is Selector) return true;
            if (d is Thumb) return true;
            if (d is ScrollBar) return true;
            if (d is Slider) return true;
            if (d is PasswordBox) return true;

            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void ReplaceOrMoveAtomic(string sourceTmp, string destPath)
    {
        if (OperatingSystem.IsWindows() && File.Exists(destPath))
        {
            var backup = destPath + ".bak";
            try
            {
                TryDeleteQuiet(backup);
                File.Replace(sourceTmp, destPath, backup, ignoreMetadataErrors: true);
            }
            finally
            {
                TryDeleteQuiet(backup);
            }
            return;
        }

        File.Move(sourceTmp, destPath, overwrite: true);
    }

    private static void TryDeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ===================== XAML handlers =====================
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (IsClickOnInteractive(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try { DragMove(); } catch { }
    }

    private void ToggleMaximizeRestore()
    {
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;

                if (_hasRestoreBounds)
                {
                    var r = ClampToWorkArea(_restoreBounds);
                    Left = r.Left;
                    Top = r.Top;
                    Width = r.Width;
                    Height = r.Height;
                }

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(16);
            }
            else
            {
                if (WindowState == WindowState.Normal)
                    UpdateRestoreBoundsFromWindow();

                WindowState = WindowState.Maximized;

                var wc = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (wc != null) wc.CornerRadius = new CornerRadius(0);
            }
        }
        catch { }
    }

    // One button: Play OR Stop (не трогаю логику)
    private void PlayOrStop_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.CanStop)
            {
                if (_vm.StopGameCommand?.CanExecute(null) == true)
                    _vm.StopGameCommand.Execute(null);
                return;
            }

            if (_vm.PlayCommand?.CanExecute(null) == true)
                _vm.PlayCommand.Execute(null);
        }
        catch { }
    }

    private void OpenSite_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        TryOpenUrl(SiteUrlPrimary);
    }

    private void OpenNewsTab_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;
            _vm.SelectedMenuIndex = NewsTabIndex;
        }
        catch { }
    }

    private void OpenNewsItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (sender is FrameworkElement fe && fe.Tag is string url && !string.IsNullOrWhiteSpace(url))
                TryOpenUrl(url);
        }
        catch { }
    }

    private async void CopyOrRegenLoginLink_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.HasLoginUrl)
            {
                if (_vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                    _vm.CopyLoginUrlCommand.Execute(null);
                return;
            }

            if (_vm.LoginViaSiteCommand?.CanExecute(null) == true)
                _vm.LoginViaSiteCommand.Execute(null);

            // Wait up to ~4.5 sec for URL to appear in VM.
            for (var i = 0; i < 30; i++)
            {
                if (_isClosing) return;

                await Task.Delay(150).ConfigureAwait(true);

                if (_vm.HasLoginUrl)
                {
                    if (_vm.CopyLoginUrlCommand?.CanExecute(null) == true)
                        _vm.CopyLoginUrlCommand.Execute(null);
                    return;
                }
            }

            if (_isClosing) return;

            MessageBox.Show(
                "Не удалось получить ссылку авторизации. Попробуйте ещё раз.",
                "Авторизация",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch { }
    }

    private void CopyLogs_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isClosing) return;

            if (_vm.LogLines is null || _vm.LogLines.Count == 0)
                return;

            var lines = _vm.LogLines.Count <= CopyLogsMaxLines
                ? _vm.LogLines.ToArray()
                : _vm.LogLines.Skip(Math.Max(0, _vm.LogLines.Count - CopyLogsMaxLines)).ToArray();

            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch { }
    }
}

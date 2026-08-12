# LegendBorn Launcher

Официальный Windows-лаунчер экосистемы LegendBorn.

Лаунчер отвечает за авторизацию через сайт, выбор и подготовку сервера, синхронизацию клиентской сборки, установку Minecraft/loader, безопасную игровую сессию, запуск игры, обновление самого лаунчера и локальную диагностику.

## Технологии

- .NET 10 / WPF
- CmlLib.Core 4.x
- NeoForge / Forge installers
- Velopack 1.2
- GitHub Actions

## Требования для разработки

- Windows 10/11
- .NET SDK 10
- Git

Сборка:

```powershell
dotnet restore LegendBorn.csproj -r win-x64
dotnet build LegendBorn.csproj -c Release --no-restore
dotnet publish LegendBorn.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish
```

CI выполняет те же проверки на Windows runner и дополнительно делает smoke-test упаковки Velopack.

## Основные части

```text
App.xaml.cs / MainWindow.*      startup, shutdown, shell UI
ViewModels/                    MVVM-состояние и команды интерфейса
Services/                      сайт, токены, обновления, конфиг, логи
Launching/                     Minecraft, loaders, pack sync
Models/                        модели API/конфига
Resources/                     темы и assets
.github/workflows/ci.yml       build/publish/package validation
.github/workflows/release.yml  Velopack release pipeline
```

## Авторизация

Долгоживущий access token сайта хранится только через Windows DPAPI (`CurrentUser`). Если DPAPI не может защитить данные, лаунчер не должен переходить на plaintext-хранение.

Minecraft не получает долгоживущий site token. Перед запуском лаунчер:

1. синхронизирует Minecraft-профиль с backend;
2. получает короткоживущий одноразовый `join-ticket`;
3. передаёт его игре через `.legendcore/session.json`;
4. очищает игровую сессию при закрытии процесса или неудачном запуске.

Старые `legendborn/auth.token` и `legendborn/auth.json`, которые могли содержать site token, считаются legacy и удаляются лаунчером.

## Данные пользователя

Основные пути создаются через `LauncherPaths`:

- `%APPDATA%\LegendBorn` — пользовательский конфиг/настройки;
- `%LOCALAPPDATA%\LegendBorn` — токены, логи, кэш и игровая директория;
- `%LOCALAPPDATA%\LegendBorn\game` — стандартный Minecraft instance.

Не храните секреты в репозитории, `appsettings`, manifest или игровых config-файлах.

## Синхронизация сборки

Pack sync использует manifest, SHA-256, зеркала/fallback, атомарную замену файлов и `.pending` для занятых файлов. Управляемые и пользовательские пути должны оставаться разделены.

Ключевое правило: пользовательские настройки нельзя уничтожать ради приведения инстанса к manifest. `mods/` и `kubejs/` могут быть managed; `config/`, `defaultconfigs/`, `resourcepacks/` и `shaderpacks/` требуют защитной политики.

## Обновление лаунчера

Версия приложения задаётся в `LegendBorn.csproj`.

При merge в `main`:

1. `CI` всегда проверяет restore/build/self-contained publish/Velopack pack;
2. `Release (Velopack)` читает версию из `.csproj`;
3. если тега `v<version>` ещё нет, собирает и публикует GitHub Release;
4. Velopack SDK и CLI `vpk` должны использовать одинаковую версию.

Не увеличивайте версию только после merge: релизный workflow запускается на самом merge-коммите.

## Перед merge

Минимальная проверка:

```powershell
dotnet restore LegendBorn.csproj -r win-x64
dotnet build LegendBorn.csproj -c Release --no-restore
dotnet publish LegendBorn.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish
```

Также проверьте вручную:

- вход/выход через сайт;
- повторный запуск с сохранённой авторизацией;
- подготовку сборки с нуля и повторный sync;
- запуск Minecraft и подключение к серверу;
- очистку `.legendcore/session.json` после выхода;
- сохранение RAM/server/settings после закрытия лаунчера;
- обновление установленной Velopack-сборки.

## Безопасность

Не публикуйте в issue/log/screenshot:

- access token;
- join-ticket;
- API keys;
- содержимое token-файла;
- приватные backend credentials.

Для диагностических сообщений логируйте тип операции, HTTP status и безопасный контекст, но не значение credential.

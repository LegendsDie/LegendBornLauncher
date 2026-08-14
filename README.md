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
dotnet build LegendBorn.csproj -c Release --no-restore -warnaserror
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
.github/workflows/release.yml  ручной Velopack production release
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

Pack sync использует manifest, SHA-256, зеркала/fallback, атомарную замену файлов и `.pending` для занятых файлов. Управляемые и пользовательские пути разделены.

Политика каталогов:

- `mods/`, `kubejs/` и `scripts/` — managed и могут синхронизироваться/prune'иться по manifest;
- `config/` и `defaultconfigs/` — **seed-only**: отсутствующий manifest-файл можно восстановить, существующий пользовательский файл нельзя перезаписать или удалить;
- `resourcepacks/` и `shaderpacks/` — user-mutable и не подвергаются destructive sync;
- потеря или повреждение `launcher/pack_state.json` не должна менять эти правила.

CI отдельно проверяет наличие destructive-managed, seed-only/user-mutable префиксов и защиту от manifest delete/prune.

## Обновление и production release лаунчера

Версия приложения задаётся в `LegendBorn.csproj`.

Merge/push в `main` **не публикует production release автоматически**. На `main` всегда запускается только `CI`, который проверяет:

1. seed-only pack policy;
2. restore для `win-x64`;
3. Release build с `-warnaserror`;
4. self-contained `win-x64` publish;
5. реальный `vpk pack` smoke-test.

Production release выполняется вручную через GitHub Actions → `Release (Velopack)` после проверки установленного лаунчера и игровой авторизации. Для запуска workflow требуется явно ввести `RELEASE` в поле подтверждения.

Release workflow:

1. читает версию приложения и версию Velopack из `.csproj`;
2. повторно выполняет restore/build/publish с warnings-as-errors;
3. проверяет существование `v<version>`;
4. собирает Velopack packages;
5. публикует GitHub Release только после успешного прохождения предыдущих шагов.

Velopack SDK и CLI `vpk` всегда должны использовать одинаковую версию.

## Перед merge

Минимальная проверка:

```powershell
dotnet restore LegendBorn.csproj -r win-x64
dotnet build LegendBorn.csproj -c Release --no-restore -warnaserror
dotnet publish LegendBorn.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/publish
```

Также проверьте вручную:

- вход/выход через сайт;
- повторный запуск с сохранённой авторизацией;
- подготовку сборки с нуля и повторный sync;
- изменение файла в `config/`, удаление `launcher/pack_state.json` и повторный sync — пользовательское изменение должно сохраниться;
- запуск Minecraft и подключение к серверу;
- фактическое чтение клиентским NeoForge-модом `.legendcore/session.json` и использование `join-ticket`;
- отсутствие зависимости актуального мода от legacy `legendborn/auth.token` / `auth.json`;
- очистку `.legendcore/session.json` после выхода;
- сохранение RAM/server/settings после закрытия лаунчера;
- обновление установленной Velopack-сборки.

## Перед production release

Production release запрещён без end-to-end проверки цепочки:

`launcher login → Minecraft link → join-ticket → game start → client mod handshake → server join → session cleanup`.

Если текущий клиентский мод всё ещё читает legacy access-token файлы, сначала обновляется мод. Возвращать долгоживущий site access token в игровой instance как временный fallback нельзя.

## Безопасность

Не публикуйте в issue/log/screenshot:

- access token;
- join-ticket;
- API keys;
- содержимое token-файла;
- приватные backend credentials.

Для диагностических сообщений логируйте тип операции, HTTP status и безопасный контекст, но не значение credential.

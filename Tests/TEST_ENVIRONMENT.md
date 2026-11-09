# Среда тестирования

## Общие требования

- .NET SDK из `global.json` (стабильный .NET 10).
- NuGet restore.
- Docker Engine для API и PostgreSQL тестов (Testcontainers).
- Никаких production secrets или прав доступа к облаку.

## Unit tests

`Tests/LocaLoca.Tests` запускается без Docker и без внешнего соединения:

```powershell
dotnet test Tests/LocaLoca.Tests/LocaLoca.Tests.csproj --configuration Release
```

## API integration

`Tests/LocaLoca.Api.Tests`:
- Testcontainers стартует `postgres:16-alpine`;
- схема загружается из `database/schema/01_schema.sql`;
- fixture создаёт синтетические category/country/city/attraction;
- WebApplicationFactory запускает API с `ASPNETCORE_ENVIRONMENT=Testing`;
- строки конфигурации, секрет сессий и каталог файлов заданы in-memory;
- пароль контейнера и HMAC secret случайные, краткоживущие;
- тесты создают случайные логины и пароли;
- контейнер и временный каталог удаляются после завершения.

Запуск:

```powershell
dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
```

## Database integration

`Tests/LocaLoca.Database.Tests` поднимает отдельную PostgreSQL 16, загружает схему и проверяет constraints, каскады и миграции.

```powershell
dotnet test Tests/LocaLoca.Database.Tests/LocaLoca.Database.Tests.csproj --configuration Release
```

Роль `localoca_app` в тестах создаётся как `NOLOGIN` без пароля. Скрипт `05_create_app_role.sql`, содержащий placeholder-пароль, не запускается. Для проверки grants используется только синтетическая роль в контейнере.

## CI

- `tests.yml`: отдельные Unit/API/Database jobs; на GitHub-hosted Ubuntu Docker уже доступен.
- `secret-scan.yml`: Gitleaks анализирует Git history и печатает findings с полным redaction.
- `windows-installer.yml`: компиляция WinUI и Inno Setup, отдельная от PostgreSQL тестов.
- `codeql.yml`: статический анализ C# и GitHub Actions.

В CI тестовым job выдаётся только `contents: read`. Они не получают Render/Supabase credentials и не загружают TRX, дампы БД, логи фикстур или другие тестовые артефакты.

## Ограничения

- Контейнеры тестов не подключаются к Supabase, Render или интернет-базам.
- Тестовые данные синтетические и уничтожаются с контейнером.
- Успешный Gitleaks scan снижает риск случайного коммита известного формата секрета, но не является математическим доказательством отсутствия любого произвольного секрета.
- Нативный WinUI/WebView2 UI требует отдельной Windows regression-сессии; Playwright не заменяет её.

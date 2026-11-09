# Среда тестирования Loca Loca

Документ описывает инструменты, конфигурацию и ограничения среды, в которой запускаются unit-, API- и PostgreSQL-тесты. Цель — воспроизводимые проверки без доступа к production Supabase, Render и пользовательским данным.

## Общие требования

- .NET SDK 10.0.100 или более поздняя версия в разрешённой ветке обновлений; версия SDK фиксируется в корневом `global.json`.
- Восстановленные NuGet-пакеты.
- Docker Engine для API- и Database-тестов через Testcontainers.
- Никаких production secrets, production connection strings или прав доступа к рабочей облачной базе.

Все команды ниже выполняются из корня репозитория.

## Unit-тесты

Проект `Tests/LocaLoca.Tests` не требует Docker, PostgreSQL или внешней сети во время выполнения тестов:

```powershell
dotnet test Tests/LocaLoca.Tests/LocaLoca.Tests.csproj --configuration Release
```

Этот слой предназначен для быстрой проверки изолированной логики: парольного хеширования, HMAC-сессий и распознавания изображений.

## API integration tests

Проект `Tests/LocaLoca.Api.Tests` поднимает отдельную PostgreSQL 16 через Testcontainers.

Порядок подготовки:

1. Testcontainers запускает образ `postgres:16-alpine`.
2. В контейнер загружается `database/schema/01_schema.sql`.
3. Fixture создаёт синтетические категории, страны, города и достопримечательности.
4. `WebApplicationFactory` запускает API с `ASPNETCORE_ENVIRONMENT=Testing`.
5. Строка подключения, секрет сессий и временный каталог задаются в памяти тестового host.
6. Пароль контейнера, HMAC secret, логины и пароли тестовых пользователей генерируются отдельно и случайно.
7. После завершения тестов контейнер и временный каталог удаляются.

Запуск:

```powershell
dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
```

Loopback PostgreSQL разрешён только в окружении `Testing`; в обычном режиме приложения локальная PostgreSQL не становится допустимой автоматически.

## Database integration tests

Проект `Tests/LocaLoca.Database.Tests` поднимает отдельную PostgreSQL 16, загружает актуальную схему и проверяет constraints, foreign keys, каскады, миграции и runtime grants.

```powershell
dotnet test Tests/LocaLoca.Database.Tests/LocaLoca.Database.Tests.csproj --configuration Release
```

Для проверки grants временно создаётся роль `localoca_app` типа `NOLOGIN` без пароля. Скрипт `05_create_app_role.sql`, содержащий placeholder-пароль для реальной настройки, в автоматическом тесте не запускается.

## CI

В GitHub Actions используются отдельные проверки:

| Workflow | Назначение |
| --- | --- |
| `tests.yml` | Отдельные Unit, API и Database jobs |
| `secret-scan.yml` | Gitleaks: поиск секретов в истории с redaction совпадений |
| `windows-installer.yml` | Windows-сборка WinUI и Inno Setup |
| `codeql.yml` | Статический анализ C# и GitHub Actions |

Для тестовых jobs предусмотрены минимальные права, необходимые для чтения репозитория. Они не получают Render/Supabase credentials и не загружают TRX, дампы базы, логи фикстур или другие артефакты, которые могут содержать тестовые данные.

## Запреты и границы изоляции

- Не направляйте тесты на production Supabase или Render.
- Не передавайте тестовым процессам реальные `LOCA_DB_CONNECTION` и `LOCA_SESSION_SECRET`.
- Не используйте реальные пользовательские логины, пароли, фотографии или выгрузки базы.
- Не коммитьте локальные конфигурации, секреты и результаты тестов.
- Не трактуйте успешное сканирование Gitleaks как математическое доказательство отсутствия любых возможных секретов.

Тестовые данные синтетические и уничтожаются вместе с контейнером или временным каталогом.

## Ограничения текущей среды

- Внешние endpoint Render и production Supabase не проверяются.
- Реальная доступность тайлов карты и внешних сервисов не проверяется.
- Нативные WinUI 3 и WebView2 требуют отдельной Windows regression-сессии.
- Playwright не заменяет тестирование WinUI.
- Успешные API- и Database-тесты не доказывают корректность установки, обновления и удаления приложения на рабочем Windows-компьютере.

# Тестовая инфраструктура Loca Loca

**Статус:** проектирование; это спецификация будущего запуска, а не готовая конфигурация тестов.

## 1. Среда

| Компонент | Требование |
|---|---|
| .NET SDK | .NET 10; репозиторий закрепляет SDK через `global.json` (10.0.100, rollForward latestFeature) |
| API | ASP.NET Core, `net10.0` |
| Desktop UI | Windows 10 build 19041 или новее; .NET 10 Windows target; WinUI 3 / WebView2 |
| PostgreSQL | Версия того же major-релиза, что и у целевой облачной БД |
| Контейнеры | Docker для Testcontainers и изолированной БД |
| CI | GitHub Actions; Linux для unit/API/DB при отсутствии Windows-specific зависимостей, Windows для installer/UI |

## 2. Предлагаемые проекты и зависимости

### `Tests/LocaLoca.Tests`

- Target framework: `net10.0`.
- xUnit, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`.
- Ссылка на будущую библиотеку `LocaLoca.Core`, но не на WinUI executable project.
- Каталоги: `Unit/`, `TestData/`.

Сюда помещать только независимую от Windows UI логику: правила валидации, преобразование моделей, построение URI и другие функции после выделения в Core. Не тестировать простые auto-properties ради процента покрытия.

### `Tests/LocaLoca.Api.Tests`

- Target framework: `net10.0`.
- xUnit, `Microsoft.AspNetCore.Mvc.Testing` версии 10.x, `Testcontainers.PostgreSql`, `Npgsql`.
- Ссылка на `Server/LocaLoca.Api/LocaLoca.Api.csproj`.
- Каталоги: `Integration/`, `Fixtures/`, `Builders/`.

Проверять API через HTTP-клиент test host. Для запуска через `WebApplicationFactory` добавить публичный partial entry point для `Program`. Все подключения должны вести в контейнерную тестовую БД.

### `Tests/LocaLoca.Database.Tests`

- Target framework: `net10.0`.
- xUnit, `Testcontainers.PostgreSql`, `Npgsql`.
- Каталоги: `Schema/`, `Migrations/`, `Constraints/`.

Тестировать SQL напрямую. Сценарии должны работать на чистой временной БД и не зависеть от наличия данных на Supabase.

## 3. Подготовка тестовой БД

Один общий fixture на тестовую коллекцию:

1. Запустить PostgreSQL-контейнер с образом версии, согласованной с целевой БД.
2. Создать временную базу и применить `database/01_schema.sql`, затем `database/02_seed.sql`.
3. Создать тестовую роль `localoca_app` с уникальным случайным паролем, минимально необходимыми правами и без SUPERUSER/CREATEDB/CREATEROLE/BYPASSRLS.
4. Применить необходимые grant/migration scripts в том же порядке, что и для production. Скрипт `05_create_app_role.sql` содержит пароль-заглушку; его нельзя запускать как есть в CI. Роль для тестов должна создаваться fixture-кодом с временным секретом.
5. Проверить `/ready` через подключение API под ролью приложения.
6. По окончании тестовой коллекции удалить контейнер.

Текущий `01_schema.sql` уже содержит персональные точки и бинарные поля фото. Миграцию `07_migrate_photo_storage.sql` следует проверять на повторный запуск и GRANT. Для upgrade-теста со старой БД нужен отдельный исторический DDL fixture; текущий `01_schema.sql` не является старой схемой.

## 4. Конфигурация API в тестах

API читает настройки при создании приложения:

- `LOCA_DB_CONNECTION` — только connection string временной PostgreSQL.
- `LOCA_SESSION_SECRET` — случайная тестовая строка не короче 32 символов.
- `PORT` — в тестовом процессе не задавать без необходимости; ветка запуска с PORT включает дополнительную проверку секрета для облачного хостинга.

Нельзя помещать значения production в `appsettings.json`, GitHub Actions YAML или тестовые файлы. Не использовать `LOCA_DB_CONNECTION` от Render/Supabase.

Сейчас connection string считывается из process environment при старте API. Пока конфигурация не вынесена в отдельный сервис, тестовый API host должен быть один на fixture; параллельный запуск нескольких host с разными строками подключения опасен из-за process-wide environment variables. Для устойчивой параллельности предпочтительна фабрика NpgsqlConnection/NpgsqlDataSource через DI.

## 5. Изоляция и очистка данных

- API-тесты должны работать под ролью приложения, а не под владельцем схемы.
- Использовать два тестовых аккаунта для сценариев ownership.
- Создавать логины с префиксом `it_` и случайным суффиксом.
- Не использовать общую базу между локальными запусками, CI и production.
- Не рассчитывать на rollback внешней транзакцией: API открывает собственные Npgsql connections и транзакции. Для очистки тестов использовать отдельную БД на запуск/fixture либо контролируемый reset таблиц между сценариями.
- Удалять временные файлы и контейнеры в `DisposeAsync`, включая сценарии падения теста.
- Не включать в логи пароли, bearer-токены, connection strings и содержимое загруженных фотографий.

## 6. Предлагаемые команды после реализации проектов

Из корня репозитория:

```powershell
dotnet restore Tests/LocaLoca.Tests/LocaLoca.Tests.csproj
dotnet test Tests/LocaLoca.Tests/LocaLoca.Tests.csproj --configuration Release

dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
dotnet test Tests/LocaLoca.Database.Tests/LocaLoca.Database.Tests.csproj --configuration Release
```

Эти команды являются целевым интерфейсом запуска. До добавления соответствующих `.csproj` выполнять их нельзя — проектов пока нет.

## 7. CI workflow

После реализации первых тестов добавить отдельный workflow, например `.github/workflows/tests.yml`:

- trigger: `pull_request` в `main`, `push` в тестовую/рабочие feature-ветки и `workflow_dispatch`;
- job Unit: setup-dotnet, restore, `dotnet test` unit-проекта;
- job API: PostgreSQL через Testcontainers, запуск API integration-тестов;
- job Database: SQL/schema/migration-набор на отдельном контейнере;
- job Build: сборка API и desktop solution;
- job UI/Installer: отдельный Windows runner или существующий installer workflow.

Не запускать тесты против опубликованного Render API или реальной Supabase. Отдельно сохранять TRX/JUnit результаты как artifacts. На первом этапе не вводить обязательный процент coverage: сначала обеспечить прохождение всех P0 кейсов.

## 8. Ручное тестирование UI и установщика

Ручной прогон выполнять на чистой Windows VM и на VM с предыдущей версией приложения. Для каждого прогона фиксировать:
- версию Windows и WebView2 Runtime;
- commit/tag и SHA установщика;
- состояние локальной папки `%LOCALAPPDATA%\\LocaLoca` до и после;
- API endpoint и состояние readiness (без публикации секретов);
- шаги воспроизведения и фактический результат.

Для release smoke обязательны сценарии UI-001–UI-008 и INST-001–INST-005 из `TEST_CASES.md`.


## 9. Фактически реализованный первый этап

В `Tests/LocaLoca.Api.Tests` расположен первый исполняемый набор API contract tests. Он не использует Docker, PostgreSQL, Supabase, Render, Playwright, браузерные плагины или ключи API.

Для тестового запуска API получает конфигурацию через in-memory provider:
- `LOCA_DB_CONNECTION=CHANGE_ME` — намеренно невалидная заглушка, при которой `/ready` сразу возвращает 503;
- `LOCA_SESSION_SECRET` — отдельный тестовый секрет;
- `LOCA_DATA_ROOT` — уникальная временная папка тестового процесса;
- `PORT` — пустое значение, чтобы не включать cloud-hosting branch.

Чтение настроек API переведено на стандартный `IConfiguration`. Переменные окружения продолжают поддерживаться стандартным .NET Environment Variables provider, но теперь тест может безопасно перекрыть их значениями in-memory, не меняя глобальное окружение процесса.

Команда запуска:

```powershell
dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
```

Команда не затрагивает рабочую БД. Она всё ещё требует восстановленных NuGet-пакетов; сами тесты выполняются локально и изолированно.

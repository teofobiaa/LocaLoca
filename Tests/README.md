# Автоматизированные тесты Loca Loca

В каталоге `Tests/` находятся отдельные проекты для проверки логики приложения, HTTP API и PostgreSQL. Тестовый контур изолирован от production: он использует синтетические данные, временные каталоги и одноразовые PostgreSQL-контейнеры.

## Что входит в тестовый набор

Тесты собраны в отдельном решении `Tests/LocaLoca.Tests.sln`. Оно отделено от основного `LocaLoca.sln`, чтобы тестовый контур можно было запускать в CI на Linux, не собирая WinUI-клиент.

| Проект | Уровень | Что проверяет | Docker |
| --- | --- | --- | --- |
| `LocaLoca.Tests` | Unit | PBKDF2-хеширование паролей, HMAC-сессии, сигнатуры и MIME изображений | Нет |
| `LocaLoca.Api.Tests` | API integration | HTTP-маршруты, регистрацию и вход, авторизацию, изоляцию пользователей, посещения, wishlist, личные точки и фотографии | Да |
| `LocaLoca.Database.Tests` | Database integration | PostgreSQL-схему, ограничения, внешние ключи, каскады, миграции и runtime-права | Да |

Полный список сценариев и их текущий статус находится в [TEST_CASES.md](TEST_CASES.md). Общие приоритеты и критерии готовности изменения описаны в [TEST_PLAN.md](TEST_PLAN.md).

## Требования

- .NET SDK 10.0.100 или более поздняя версия в разрешённой ветке обновлений; версия проекта зафиксирована в корневом `global.json`.
- Docker Desktop с работающим Linux-контейнерным движком для API- и Database-тестов.
- Доступ к NuGet для первичного восстановления пакетов.

Unit-тесты не требуют Docker и внешней базы данных. API- и Database-тесты запускают PostgreSQL 16 через Testcontainers.

## Запуск всех тестов

Из корня репозитория:

```powershell
dotnet test Tests/LocaLoca.Tests.sln --configuration Release
```

Первый запуск может потребовать восстановления NuGet-пакетов и загрузки Docker-образа PostgreSQL.

## Запуск отдельных уровней

### Unit

```powershell
dotnet test Tests/LocaLoca.Tests/LocaLoca.Tests.csproj --configuration Release
```

Не требует Docker, PostgreSQL или production-конфигурации.

### API integration

```powershell
dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
```

Тестовый host запускает ASP.NET Core через `WebApplicationFactory<Program>`, а БД берёт из одноразового PostgreSQL-контейнера.

### Database integration

```powershell
dotnet test Tests/LocaLoca.Database.Tests/LocaLoca.Database.Tests.csproj --configuration Release
```

Тесты копируют все SQL-файлы `database/`, проверяют инвентарь и отсутствие внешних connection strings/psql-команд, затем применяют актуальную схему и миграции в одноразовой PostgreSQL 16. Проверяются foreign keys, индексы, CHECK/UNIQUE constraints, каскады и повторное выполнение миграций `03`, `04`, `06`, `07`.

## Изоляция и безопасная работа с данными

- Тесты не должны читать `LOCA_DB_CONNECTION`, `LOCA_SESSION_SECRET` или иные секреты Render из окружения CI.
- API test host получает строку подключения к созданному Testcontainers-контейнеру, случайный HMAC-секрет и отдельный временный каталог.
- Loopback PostgreSQL разрешён только при `ASPNETCORE_ENVIRONMENT=Testing`; обычный режим приложения по-прежнему отвергает локальную PostgreSQL.
- Логины и пароли тестовых пользователей синтетические; секреты не выводятся в логи.
- Database-тесты запускают `05_create_app_role.sql` только внутри одноразового PostgreSQL-контейнера, затем проверяют runtime-права и отсутствие административных привилегий. Этот скрипт не запускается в Supabase или иной внешней базе.
- Тестовые контейнеры и временные каталоги удаляются после завершения.
- CI jobs тестирования получают только необходимые права чтения репозитория; им не передаются production credentials.

Не запускайте тесты против рабочей Supabase-базы и не подставляйте production connection string в переменные окружения тестового процесса.

## CI и дополнительные проверки

В репозитории предусмотрены отдельные workflow для тестов, сканирования секретов, CodeQL и Windows-сборки/установщика. Успешный запуск unit/API/database jobs не заменяет Windows regression-проверку нативного WinUI 3, WebView2 и Inno Setup.

## Что не покрыто этим набором

Текущий автоматизированный тестовый контур не проверяет:

- реальную доступность Render и production Supabase;
- внешние тайлы карты, почтовые сервисы и сторонние интеграции;
- полный пользовательский интерфейс WinUI/WebView2;
- визуальное качество и удобство интерфейса;
- сценарии установки и удаления Windows-программы end-to-end.

Для этих областей нужна отдельная контролируемая Windows regression-сессия или инфраструктура интеграционного тестирования. Playwright не является заменой тестированию WinUI.

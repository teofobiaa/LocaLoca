# Автоматизированное тестирование Loca Loca

Тесты намеренно остаются в публичном репозитории. Они не содержат пользовательских данных, production-паролей, токенов или рабочих строк подключения. Тестовые учётные данные создаются в памяти или генерируются случайно во время теста.

## Структура

| Проект | Что проверяет | Нужен Docker |
|---|---|---:|
| `LocaLoca.Tests` | Чистая логика: PBKDF2-хеширование пароля, сессии HMAC, сигнатуры изображений | Нет |
| `LocaLoca.Api.Tests` | HTTP API, регистрацию и вход, авторизацию, изоляцию двух пользователей, посещения, wishlist, личные точки, фото | Да |
| `LocaLoca.Database.Tests` | PostgreSQL-схему, constraints, foreign keys, каскады, миграции и runtime-права | Да |

Тестовые проекты собраны в отдельном `Tests/LocaLoca.Tests.sln`. Основной `LocaLoca.sln` остаётся Windows/WinUI-решением и не вынужден собирать тестовый контур на Linux.

## Запуск локально

Требуются .NET 10 SDK и Docker Desktop с запущенным Linux-контейнерным движком.

Из корня репозитория:

```powershell
dotnet test Tests/LocaLoca.Tests.sln --configuration Release
```

Или отдельные слои:

```powershell
dotnet test Tests/LocaLoca.Tests/LocaLoca.Tests.csproj --configuration Release
dotnet test Tests/LocaLoca.Api.Tests/LocaLoca.Api.Tests.csproj --configuration Release
dotnet test Tests/LocaLoca.Database.Tests/LocaLoca.Database.Tests.csproj --configuration Release
```

Первый запуск восстанавливает NuGet-пакеты. API- и Database-тесты создают одноразовые контейнеры PostgreSQL 16, выполняют тесты на синтетических данных и удаляют контейнеры после завершения.

## Границы доступа и секреты

- Тесты не читают `LOCA_DB_CONNECTION`, `LOCA_SESSION_SECRET` или другие Render secrets из окружения CI.
- API test host получает отдельную строку подключения из Testcontainers, случайный тестовый HMAC-секрет и уникальную временную папку.
- Loopback PostgreSQL разрешён только при `ASPNETCORE_ENVIRONMENT=Testing`; обычный режим приложения по-прежнему отвергает локальный PostgreSQL.
- Пароли тестовых пользователей создаются случайно и не выводятся в лог.
- Роль `localoca_app` в database-тесте — временная `NOLOGIN`-роль без пароля. Скрипт `05_create_app_role.sql` с placeholder-паролем в автоматических тестах не используется.
- Workflow тестов не передаёт им секреты и не загружает тестовые артефакты с данными.
- `.gitignore` исключает локальные конфигурации, результаты тестов и локальные secrets-файлы.

## UI и установщик

Native WinUI 3, WebView2, регистрационная форма, смена темы, страница профиля, карта и Inno Setup проверяются отдельным Windows regression-набором. Playwright не является заменой тестированию WinUI. Автоматизацию UI можно добавить позже отдельным Windows-only проектом, например на FlaUI.

## Что пока не является целью

Тесты не выполняют запросы к production Supabase или Render, не проверяют реальную доступность внешних тайлов карт и почтовых сервисов и не меняют облачную базу. Для production-интеграций нужны отдельные контролируемые проверки с ограниченными тестовыми аккаунтами и отдельной базой; это не часть текущего CI.

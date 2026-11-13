# Развёртывание Loca Loca API на Render

Документ описывает публикацию ASP.NET Core API из этого репозитория в Render, настройку подключения к Supabase PostgreSQL и проверку готовности сервиса. Render Blueprint хранится в корне репозитория в файле `render.yaml`, Dockerfile — в `Server/LocaLoca.Api/Dockerfile`.

## Что потребуется

- GitHub-репозиторий `teofobiaa/LocaLoca` с доступом к ветке `main`;
- аккаунт Render с подключённым GitHub;
- проект Supabase PostgreSQL;
- строка подключения Supabase Session Pooler для отдельной роли приложения `localoca_app.<project-ref>`;
- возможность создать и безопасно хранить секрет подписи сессий.

Blueprint описывает один Docker Web Service `localoca-api` на Free plan. Автоматический деплой коммитов отключён: публикацией управляет GitHub Actions после успешных .NET-тестов и сборки Docker-образа.

## Конфигурация секретов

При создании или настройке сервиса задай две переменные окружения.

| Переменная | Назначение | Требования |
| --- | --- | --- |
| `LOCA_DB_CONNECTION` | Подключение API к Supabase PostgreSQL | Session Pooler, роль `localoca_app.<project-ref>`, TLS |
| `LOCA_SESSION_SECRET` | Подпись Bearer-сессий | Случайное значение не короче 32 символов |

Сгенерировать значение для `LOCA_SESSION_SECRET` можно локально в PowerShell:

```powershell
[Convert]::ToBase64String(
    [Security.Cryptography.RandomNumberGenerator]::GetBytes(48)
)
```

Сохраняй оба значения только в защищённых настройках Render. Не добавляй их в `render.yaml`, исходный код, Dockerfile, клиент, установщик или коммиты. Для настройки самой базы и роли см. [database/README.md](../../database/README.md). Для новой пустой базы используй полный снимок `database/schema/01_schema.sql` версии 011: он уже содержит `user_sessions`. Для существующей базы сначала выполни `database/migrations/00_migration_history.sql`, проверь журнал `public.localoca_schema_migrations` и примени отсутствующие миграции в числовом порядке. Подробный порядок и контрольные суммы закреплены в [реестре версий](../../database/migrations/manifest.json) и [инструкции по БД](../../database/README.md). Права уже существующей runtime-роли при необходимости обнови скриптом `database/security/06_update_runtime_permissions.sql`.

## Первичное развёртывание

1. Открой [Render Blueprints](https://dashboard.render.com/blueprints).
2. Выбери **New + → Blueprint** и подключи GitHub, если это ещё не сделано.
3. Выбери репозиторий `teofobiaa/LocaLoca` и ветку `main`.
4. Проверь параметры Blueprint: сервис `localoca-api`, runtime Docker, Free plan, health check path `/health`.
5. Примени Blueprint.
6. Добавь `LOCA_DB_CONNECTION` и `LOCA_SESSION_SECRET` в переменные окружения сервиса.
7. Сохрани настройки и дождись завершения деплоя. При необходимости изучи вкладки **Events** и **Logs** в Render.
8. Скопируй публичный адрес сервиса вида `https://<service>.onrender.com` со страницы Overview.

Render передаёт контейнеру переменную `PORT`. API слушает `0.0.0.0:$PORT`; локальный режим разработки использует `http://127.0.0.1:5181`.

## GitHub Actions: CI/CD и автоматический деплой

Workflow `.github/workflows/api-docker-build.yml` выполняет для pull request и push в поддерживаемые ветки:

1. `dotnet restore Tests/LocaLoca.Tests.sln`;
2. `dotnet build Server/LocaLoca.Api/LocaLoca.Api.csproj --configuration Release`;
3. `dotnet test Tests/LocaLoca.Tests.sln` — unit-, API- и PostgreSQL integration tests (последние запускают изолированные PostgreSQL-контейнеры);
4. сборку Docker-образа API; при push в `main` или ручном запуске workflow из `main` — публикацию образа в GitHub Container Registry (`ghcr.io/teofobiaa/localoca-api`) с тегами `latest` и коротким SHA коммита.

Только push в `main`, прошедший оба CI job, запускает Render Deploy Hook. После запроса workflow ожидает, пока оба endpoint вернут HTTP 200: `/health` и `/ready`. Если проверка готовности БД не проходит в течение 10 минут, deploy job завершается ошибкой.

### Однократная настройка GitHub

В настройках репозитория открой **Settings → Secrets and variables → Actions → New repository secret** и добавь:

| Secret | Значение |
| --- | --- |
| `RENDER_DEPLOY_HOOK` | URL Deploy Hook для сервиса `localoca-api` из Render → Settings |
| `RENDER_SERVICE_URL` | Публичный URL сервиса, например `https://localoca-api.onrender.com` |

Не добавляй эти URL в исходный код. Deploy Hook является секретом, поскольку любой обладатель URL может инициировать деплой.

### Важные условия

- В Render Blueprint должно быть применено `autoDeployTrigger: off`. Иначе Render сможет начать деплой до завершения GitHub CI.
- Секреты `LOCA_DB_CONNECTION` и `LOCA_SESSION_SECRET` остаются только в переменных окружения Render.
- `/health` проверяет доступность процесса API; `/ready` дополнительно проверяет соединение с PostgreSQL, схему и права runtime-роли.
- GitHub Actions публикует Docker-образ в GHCR только после успешных .NET-тестов и только для `main`; pull request и feature-ветки выполняют проверочную сборку без публикации. Для push используется встроенный `GITHUB_TOKEN` с правом `packages: write`.
- Pull request и push в feature-ветки проходят CI и Docker build, но не запускают production deploy.


## Проверка после деплоя

Проверь два endpoint по адресу своего сервиса:

| Endpoint | Что проверяет | Ожидаемый результат |
| --- | --- | --- |
| `/health` | Liveness API — отвечает ли процесс | HTTP 200 |
| `/ready` | Подключение к PostgreSQL, наличие необходимых объектов схемы и runtime-разрешений | HTTP 200 при корректной настройке |

Пример:

```text
https://<service>.onrender.com/health
https://<service>.onrender.com/ready
```

Успешный ответ `/health` **не доказывает**, что база данных доступна. Для проверки базы и разрешений используй `/ready`. Если `/ready` возвращает HTTP 503, проверь строку подключения, применённые SQL-миграции и права роли `localoca_app`.

## Хранение фотографий

Фотографии записываются в PostgreSQL в столбцы `bytea` таблиц `public.photos` и `public.user_map_place_photos`. Байты изображения и MIME-тип сохраняются вместе с метаданными. Новые загрузки не зависят от временной файловой системы Render и входят в резервные копии PostgreSQL.

**Для существующей базы до деплоя API проверь журнал версий и наличие миграций 007 и 008.** Если записи или объекты отсутствуют, примени `database/migrations/07_migrate_photo_storage.sql` и/или `database/migrations/08_server_sessions.sql` в Supabase SQL Editor под ролью администратора. Без столбцов фото, таблицы `public.user_sessions` и полей проживания из миграции 011 проверка `/ready` не пройдёт. Для существующей базы также примени `database/migrations/11_user_residence_location.sql` и обнови права через `database/security/06_update_runtime_permissions.sql`. В новой базе все эти объекты уже входят в `schema/01_schema.sql` версии 009.

Совместимость со старыми файлами, оставшимися на сервере от прежней файловой реализации, не гарантирует их восстановление: временная файловая система Render может быть очищена. Эта миграция не восстанавливает файлы, уже потерянные с диска.

Ограничения одной операции загрузки:

- не более 10 изображений;
- суммарный размер — не более 20 МиБ;
- форматы: JPG, PNG, WEBP, BMP и GIF.

## Подключение Windows-клиента

После успешной проверки `/health` и `/ready` укажи URL Render в пользовательской переменной Windows. Выполни в PowerShell на компьютере пользователя, заменив адрес на фактический:

```powershell
[Environment]::SetEnvironmentVariable(
    "LOCA_API_BASE_URL",
    "https://<service>.onrender.com",
    "User"
)
```

Закрой и снова открой Loca Loca. Если переменная задана, клиент использует облачный API. При недоступности удалённого endpoint он не запускает локальный API автоматически.

Чтобы удалить пользовательскую настройку и вернуться к локальному режиму разработки:

```powershell
[Environment]::SetEnvironmentVariable("LOCA_API_BASE_URL", $null, "User")
```

## Публикуемый Docker-пакет GHCR

После успешного workflow образ доступен в [GitHub Packages](https://github.com/teofobiaa/LocaLoca/packages) как `ghcr.io/teofobiaa/localoca-api`. Для загрузки:

```powershell
docker pull ghcr.io/teofobiaa/localoca-api:latest
```

Для приватного пакета сначала выполни `docker login ghcr.io`; для публичного пакета вход не требуется. Видимость пакета (Public/Private) настраивается в настройках самого пакета GitHub.

## Локальная проверка Docker-образа

Из корня репозитория:

```powershell
docker build -f Server/LocaLoca.Api/Dockerfile -t localoca-api .
docker run --rm -p 10000:10000 `
  -e PORT=10000 `
  -e LOCA_DB_CONNECTION='<строка подключения для локальной проверки>' `
  -e LOCA_SESSION_SECRET='<случайный секрет не короче 32 символов>' `
  localoca-api
```

Затем проверь `http://localhost:10000/health` и `http://localhost:10000/ready`. Не сохраняй реальные значения переменных в PowerShell-скриптах или истории команд общего компьютера.

Сборка контейнера подтверждает только то, что Docker-образ может быть собран. Она не подтверждает корректность прав PostgreSQL, доступность базы, работу сессий, сохранность фотографий или соединение Windows-клиента.

## Эксплуатационные особенности и диагностика

- **Сервис не отвечает.** Проверь состояние последнего деплоя и логи Render, затем `/health`.
- **`/health` отвечает, а `/ready` нет.** Проверь `LOCA_DB_CONNECTION`, доступность Supabase Session Pooler, TLS, миграции и grants runtime-роли.
- **После смены `LOCA_SESSION_SECRET` пользователи вышли из аккаунтов.** Это ожидаемо: токены подписаны прежним секретом. Сохраняй секрет неизменным между деплоями.
- **Клиент не подключается к Render.** Проверь `LOCA_API_BASE_URL`, HTTPS-адрес и доступность endpoint.
- **Первый запрос долго выполняется после простоя.** Бесплатный Render-сервис может засыпать после периода без запросов; первый последующий запрос может быть медленнее.

Bearer-токены действуют 30 дней. При локальной разработке используется временный секрет, поэтому перезапуск локального API инвалидирует выданные им сессии.

Развёртывание рассчитано на учебный проект. Оно не является подтверждением готовности к production-нагрузке и не заменяет резервное копирование, мониторинг и аудит безопасности.

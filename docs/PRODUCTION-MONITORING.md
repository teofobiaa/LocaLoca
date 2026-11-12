# Production Monitoring

Мониторинг выполняется GitHub Actions workflow `.github/workflows/production-monitoring.yml`.

## Что проверяется

- Публичный сайт Loca Loca.
- Render API: `/health` (liveness).
- Render API: `/ready` (PostgreSQL, схема и runtime permissions).
- Любой сетевой сбой или HTTP-код вне диапазона 2xx, включая 5xx, считается ошибкой.

Проверки запускаются каждые 15 минут. Ежедневно в 08:00 UTC формируется сводка в GitHub Actions job summary. Также workflow можно запустить вручную.

## Однократная настройка

В GitHub открой **Settings → Secrets and variables → Actions → Variables** и создай:

| Variable | Значение |
| --- | --- |
| `PRODUCTION_API_URL` | Публичный URL Render, например `https://имя-сервиса.onrender.com` |
| `PRODUCTION_SITE_URL` | Необязательно; по умолчанию используется `https://teofobiaa.github.io/LocaLoca/` |

Не добавляй секреты базы данных или сессий в Variables или workflow. Мониторингу нужны только публичные URL.

## Уведомления

При сбое workflow завершается с ошибкой и GitHub Actions отправляет уведомления подписчикам репозитория в соответствии с их настройками GitHub Notifications. Включи уведомления о **Actions → Failed workflows** в настройках уведомлений GitHub. Восстановление видно по следующему успешному запуску.

## Измерение времени HTTP и SQL

API публикует метрики через `System.Diagnostics.Metrics` в meter `LocaLoca.Api.Performance`:

| Метрика | Единицы | Измеряет |
| --- | --- | --- |
| `loca.http.request.duration` | ms | Полное время обработки HTTP-запроса |
| `loca.db.command.duration` | ms | Время выполнения команды PostgreSQL по Npgsql Activity |

HTTP-метрика содержит низкокардинальные теги HTTP method, route template и status code. SQL-метрика содержит тип операции и признак ошибки. Текст SQL и параметры не используются как теги метрик.

Медленные операции дополнительно записываются в лог API на уровне Warning. Порог по умолчанию — 500 мс для HTTP и 100 мс для SQL. Все остальные измерения доступны на уровне Debug. Пороги можно переопределить переменными окружения Render:

| Variable | Значение по умолчанию |
| --- | ---: |
| `Performance__SlowHttpRequestThresholdMs` | `500` |
| `Performance__SlowSqlCommandThresholdMs` | `100` |

Для SQL в лог записывается короткий SHA-256 fingerprint текста команды, тип операции, длительность и признак ошибки; сами SQL-команды и параметры не записываются. Метрики публикуются внутри процесса и могут быть подключены к сборщику `System.Diagnostics.Metrics` / OpenTelemetry. Экспортёр и внешнее хранилище метрик этим изменением не добавляются.

## Ограничения

- GitHub Actions schedule не является системой real-time мониторинга: запуск может задерживаться, особенно при высокой нагрузке GitHub.
- Render Free может усыплять сервис; первый запрос после простоя способен быть медленным. Проверка делает ограниченные повторы, но не предотвращает cold start.
- `/health` проверяет процесс API, а `/ready` — подключение и необходимые права PostgreSQL.
- Проверка сайта подтверждает HTTP-доступность, но не проверяет пользовательский сценарий в браузере.
- Для независимых уведомлений в Slack/Telegram и гарантированного дежурства потребуется отдельный webhook/monitoring provider.

## Ручной запуск

Открой **Actions → Production Monitoring → Run workflow**. При необходимости можно передать API URL для конкретного запуска.

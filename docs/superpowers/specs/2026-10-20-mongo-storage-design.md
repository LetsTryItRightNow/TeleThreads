# Хранилище на MongoDB вместо SQLite — дизайн

Дата: 2026-10-20. Дополняет `2026-10-19-telethreads-design.md` (раздел «Модель данных»).

## Цель

Полностью заменить SQLite на MongoDB в отдельном контейнере. Поведение сервиса
(очередь постов, статусы, повторы, API, аккаунт Threads) не меняется — меняется только
реализация хранилища. Данные из SQLite не переносятся (там только тестовые записи).

## Docker

- `docker-compose.yml`: новый сервис `mongo` (`mongo:8.0`), том `telethreads-mongo`
  (`name: telethreads-mongo`) на `/data/db`, порт `127.0.0.1:27017:27017`
  (только локально на сервере; доступ из Compass — через SSH-туннель).
- Root-пользователь: `MONGO_INITDB_ROOT_USERNAME` / `MONGO_INITDB_ROOT_PASSWORD` из `.env`
  (переменные `MONGO_USER`, `MONGO_PASSWORD`).
- Healthcheck: `mongosh --quiet --eval "db.adminCommand('ping').ok"`; приложение
  `depends_on: mongo: condition: service_healthy`.
- Приложению compose передаёт
  `Mongo__ConnectionString=mongodb://${MONGO_USER}:${MONGO_PASSWORD}@mongo:27017/?authSource=admin`.
  Пароль генерируется `openssl rand -hex 16`, чтобы не требовалось URL-экранирование.
- Том `/data` приложения остаётся — только для фото (`/data/media`).

## Настройки

`MongoOptions`: `ConnectionString` (по умолчанию `mongodb://localhost:27017`),
`Database` (по умолчанию `telethreads`). `StorageOptions.DatabasePath` удаляется,
`DataPath`/`MediaRoot` остаются.

## Данные

**`posts`** — документ на пост, имена полей в camelCase:
`_id` (long, из счётчика), `telegramChatId`, `telegramFirstMessageId`, `telegramMessageIds`
(массив), `mediaGroupId`, `telegramDate`, `originalText`, `imageFiles` (массив; `imageCount`
по-прежнему вычисляется), `rewrittenText`, `status`, `attempts`, `lastError`, `nextAttemptAt`,
`threadsPostId`, `receivedAt`, `rewrittenAt`, `publishedAt`, `updatedAt`. Даты — BSON Date (UTC).

Индексы: уникальный `(telegramChatId, telegramFirstMessageId)`; `(status, nextAttemptAt, _id)`.

**`counters`** — `{ _id: "posts", seq }`; новый id — `findOneAndUpdate` с `$inc` и upsert.
Числовые id сохраняют совместимость API (`/api/posts/{id}`).

**`settings`** — `{ _id: key, value }` (аккаунт и токен Threads, как сейчас).

## Операции очереди

- `ClaimNext(from, to, now)` — одна операция `findOneAndUpdate`: фильтр
  `status == from && (nextAttemptAt == null || nextAttemptAt <= now)`, сортировка по `_id`,
  `$set status/updatedAt`, возврат документа после изменения.
- `RecordFailure(...)` — один `findOneAndUpdate` с aggregation pipeline: `attempts + 1`,
  `status = attempts+1 >= max ? Failed : retry`, `lastError`, `nextAttemptAt`, `updatedAt`;
  возвращает новый статус.
- `ResetInProgress(now)` — `updateMany` для `Rewriting → Received` и `Publishing → Rewritten`.
- `Exists`, `Insert`, `Get`, `List(limit, status)` (новые сверху), `MarkRewritten`,
  `MarkPublished`, `GetSetting`, `SetSetting` (upsert), `DeleteSetting`.

Публичные сигнатуры `PostRepository` не меняются (синхронные методы драйвера), поэтому
`PostProcessor`, сканер, API и Threads-код не трогаются. `Initialize()` создаёт индексы.

## Удаляется

Пакет `Microsoft.Data.Sqlite`, SQL-код, `StorageOptions.DatabasePath`.

## Тесты

Тесты, использующие хранилище, работают с настоящей MongoDB: строка подключения из
переменной окружения `TELETHREADS_TEST_MONGO`, по умолчанию `mongodb://localhost:27017`.
`TempDataDir` создаёт уникальную базу `telethreads_test_<guid>` и удаляет её в `Dispose`.
Все существующие тесты репозитория сохраняются (поведение то же).

## Документация

`docs/deploy.md`: новые переменные в `.env` (`MONGO_USER`, `MONGO_PASSWORD`), обновление
(`docker compose up -d --build` поднимет и Mongo), подключение Compass через
`ssh -L 27017:127.0.0.1:27017 <сервер>` и строку
`mongodb://<user>:<password>@localhost:27017/?authSource=admin`, примеры `mongosh`
(`docker compose exec mongo mongosh -u ... -p ...`), резервная копия через `mongodump`.

# TeleThreads — дизайн базовой версии

Дата: 2026-10-19

## Цель

Сервис читает новые посты в Telegram-канале (текст + фотографии), сокращает текст
через Gemini до формата Threads (≤ 500 символов) и публикует пост с теми же фото
в Threads. На первом этапе публикация в Threads работает в режиме **dry-run**:
всё готовится и логируется, но реальные запросы на публикацию не отправляются.

## Принятые решения

- Публикация автоматическая, без ручного одобрения.
- Telegram: бот-администратор канала, получение обновлений через **long polling**.
- Архитектура: **один сервис ASP.NET Core (.NET 10), SQLite как очередь и журнал**.
- Gemini и Threads вызываются напрямую через `HttpClient` (без SDK).
- Хранилище: SQLite + фото в каталоге `/data` (docker volume).

## Структура

```
Telegram/   TelegramPollingService  — BackgroundService, long polling, берёт только
                                      channel_post из канала Telegram:ChannelId
            MediaGroupAggregator    — собирает альбом по media_group_id; альбом считается
                                      завершённым через 3 с без новых сообщений группы
Rewriting/  IPostRewriter
            GeminiRewriter          — системный промпт из prompts/system.txt
Threads/    IThreadsPublisher
            ThreadsPublisher        — реальная публикация (TEXT / IMAGE / CAROUSEL)
            DryRunThreadsPublisher  — логирует, что было бы отправлено; ничего не шлёт
            ThreadsTokenService     — обновление long-lived токена (только в реальном режиме)
Storage/    PostRepository          — Microsoft.Data.Sqlite + Dapper
Pipeline/   PostProcessor           — BackgroundService: rewrite → publish, повторы
Api/        PostsEndpoints          — GET-API для просмотра постов и их статусов
Program.cs  /health, /media/* (раздача фото), /api/posts
```

Выбор публикатора — по `Threads:DryRun` (по умолчанию `true`).

## Модель данных

Таблица `posts`:

| Поле | Тип | Описание |
|---|---|---|
| id | INTEGER PK | внутренний id |
| telegram_chat_id | INTEGER | id канала |
| telegram_message_ids | TEXT | JSON-массив id сообщений (у альбома их несколько) |
| media_group_id | TEXT NULL | id альбома Telegram |
| telegram_date | TEXT | дата поста в Telegram (UTC, ISO 8601) |
| original_text | TEXT NULL | текст/подпись из Telegram |
| image_count | INTEGER | количество фото |
| image_files | TEXT | JSON-массив путей фото относительно /data/media (`{chatId}_{firstMessageId}/{n}.jpg`) |
| rewritten_text | TEXT NULL | текст от Gemini |
| status | TEXT | см. ниже |
| attempts | INTEGER | число попыток обработки |
| last_error | TEXT NULL | последняя ошибка |
| threads_post_id | TEXT NULL | id поста в Threads (в dry-run — `dry-run`) |
| received_at / rewritten_at / published_at / updated_at | TEXT NULL | время этапов (UTC) |

Уникальный индекс по `(telegram_chat_id, telegram_first_message_id)` —
защита от дублей.

Таблица `settings` (key/value): текущий токен Threads и дата его обновления.

### Статусы

`Received` → `Rewriting` → `Rewritten` → `Publishing` → `Published` (или `Failed`).

- `Rewriting` / `Publishing` — пост сейчас в обработке на этом этапе.
- При ошибке пост возвращается в предыдущий устойчивый статус (`Received` или
  `Rewritten`), `attempts` увеличивается, `last_error` заполняется. После 3 неудачных
  попыток — `Failed`.
- При старте сервиса посты, зависшие в `Rewriting`/`Publishing`, возвращаются в
  предыдущий устойчивый статус.
- В dry-run успешный этап публикации тоже переводит пост в `Published`, с
  `threads_post_id = "dry-run"`.

## Поток данных

1. **Telegram.** Приходит `channel_post` из нужного канала. Для каждого фото
   скачивается самый крупный размер. Одиночный пост сохраняется сразу; части альбома
   копятся в `MediaGroupAggregator` и сохраняются одной записью после 3 с тишины.
   Подпись берётся из того сообщения альбома, где она есть. Статус — `Received`.
2. **Gemini.** `PostProcessor` (опрос БД раз в 5 с) берёт пост `Received`. Если
   `original_text` пуст — `rewritten_text` = пусто, Gemini не вызывается. Иначе
   текст отправляется в Gemini (`generateContent`) с системным промптом из
   `prompts/system.txt`; **промпт явно требует результат не длиннее 500 символов**.
   Если ответ всё же длиннее 500 символов — обрезается по последней границе
   предложения (или слова) в пределах лимита. Статус — `Rewritten`.
3. **Threads.** Пост `Rewritten` передаётся в `IThreadsPublisher`:
   - 0 фото → контейнер `TEXT`;
   - 1 фото → контейнер `IMAGE` с `image_url`;
   - 2–20 фото → контейнеры-элементы (`is_carousel_item=true`) + контейнер `CAROUSEL`
     с текстом (больше 20 — берутся первые 20);
   - ожидание статуса контейнера `FINISHED` (опрос, таймаут ~1 мин);
   - `threads_publish`.
   URL фото: `{Media:PublicBaseUrl}/media/{image_file}`.
   В dry-run публикатор пишет в лог тип поста, текст и URL фото и возвращает `dry-run`.
   Статус — `Published`. Фото удаляются только после реальной публикации
   (в dry-run остаются, чтобы можно было проверить URL).

## GET-API

API открытое, без авторизации: в нём только данные, которые и так публичны.

- `GET /api/posts?limit=50&status=Failed` — список постов, новые сверху.
- `GET /api/posts/{id}` — один пост.

Ответ (на пост):

```json
{
  "id": 12,
  "telegramMessageIds": [345, 346, 347],
  "telegramDate": "2026-10-19T10:15:00Z",
  "imageCount": 3,
  "imageUrls": ["https://telethreads.dergach.xyz/media/-1001234567890_345/0.jpg", "..."],
  "originalText": "...",
  "rewrittenText": "...",
  "status": "Published",
  "attempts": 1,
  "lastError": null,
  "threadsPostId": "dry-run",
  "receivedAt": "...", "rewrittenAt": "...", "publishedAt": "..."
}
```

## Пограничные случаи

- Видео, документы и прочее — игнорируются, берутся только фото.
- Сообщения не из настроенного канала — игнорируются.
- Правки постов (`edited_channel_post`) — игнорируются.

## Конфигурация

| Ключ | Описание |
|---|---|
| `Telegram__BotToken` | токен бота |
| `Telegram__ChannelId` | id канала (например `-100...`) |
| `Gemini__ApiKey` | ключ Gemini API |
| `Gemini__Model` | модель (по умолчанию `gemini-3.5-flash` — стабильная, поддерживается минимум до 05.2027) |
| `Threads__DryRun` | `true` по умолчанию |
| `Threads__AppId` / `Threads__AppSecret` | Threads app id/secret для OAuth; аккаунт подключается через `/threads/login` (см. `2026-10-20-threads-oauth-design.md`) |
| `Media__PublicBaseUrl` | `https://telethreads.dergach.xyz` |
| `Storage__DataPath` | `/data` |

Секреты — в `.env` на сервере (не в git). docker-compose монтирует volume в `/data`.
Reverse proxy на сервере направляет `telethreads.dergach.xyz` в контейнер.

## Деплой

Деплоит пользователь сам по пошаговой инструкции (`docs/deploy.md`):
- echo-сервер на `echo.dergach.xyz` выключается, его контейнер удаляется;
- новый контейнер `telethreads` публикуется на `telethreads.dergach.xyz`
  (DNS уже настроен), прокси и сертификат переключаются на новый домен;
- `.env` с секретами создаётся на сервере по шаблону `.env.example`.

Echo-эндпоинты (`/echo`) из `Program.cs` удаляются, `/health` остаётся.

## Ошибки и логирование

Ошибки внешних API логируются через `ILogger` (видно в `docker logs`) и
записываются в `last_error`. Повторы — до 3 попыток с паузой (30 с, 2 мин).

## Тестирование

Отдельный xUnit-проект, без реальных внешних вызовов:
- `MediaGroupAggregator` — сборка альбома, подпись из любого сообщения группы;
- ограничение 500 символов (обрезка по границе предложения/слова);
- `ThreadsPublisher` — последовательность запросов для TEXT/IMAGE/CAROUSEL через
  подменённый `HttpMessageHandler`;
- `PostRepository` — переходы статусов на временной SQLite.

## Вне рамок базовой версии

Ручное одобрение, видео, правки постов, уведомления об ошибках в Telegram,
веб-интерфейс.

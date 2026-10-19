# Подключение аккаунта Threads через OAuth — дизайн

Дата: 2026-10-20. Дополняет `2026-10-19-telethreads-design.md`.

## Цель

Публиковать в аккаунт Threads, который принадлежит другому человеку, без его пароля.
Владелец открывает одну ссылку, входит в Threads у себя и нажимает «Разрешить» —
сервис сам получает и хранит токен. Токен и user id больше не вписываются в `.env` вручную.

## Как это работает

1. Разработчик создаёт приложение в Meta for Developers (сценарий Threads API,
   разрешения `threads_basic`, `threads_content_publish`), добавляет аккаунт владельца в
   Threads Testers. Владелец принимает приглашение в Threads.
2. Разработчик отправляет владельцу ссылку `https://telethreads.dergach.xyz/threads/login`.
3. `/threads/login` перенаправляет на `https://threads.net/oauth/authorize` с `client_id`,
   `redirect_uri = {Media:PublicBaseUrl}/threads/callback`, `scope`, `response_type=code` и
   одноразовым `state`.
4. `/threads/callback` проверяет `state`, обменивает `code` на короткий токен
   (`POST https://graph.threads.net/oauth/access_token`), короткий — на долгий 60-дневный
   (`GET https://graph.threads.net/access_token?grant_type=th_exchange_token`), узнаёт
   `id` и `username` (`GET /v1.0/me?fields=id,username`), сохраняет их в таблицу `settings`
   и показывает страницу «Готово, аккаунт @username подключён».
5. Токен продлевается как раньше — не чаще раза в 7 дней, при публикации.

Повторное открытие ссылки заменяет сохранённый аккаунт (так же продлевается доступ, если
токен истёк).

## Эндпоинты

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/threads/login` | редирект на авторизацию Threads; если `AppId`/`AppSecret` не заданы — страница-ошибка |
| GET | `/threads/callback` | приём кода, сохранение аккаунта, HTML-страница с результатом |
| GET | `/threads/status` | JSON: `connected`, `username`, `tokenCheckedAt`, `dryRun` (без токена) |
| POST | `/threads/uninstall` | колбэк Meta при отзыве доступа: проверить `signed_request`, удалить аккаунт |
| POST | `/threads/delete` | колбэк Meta на удаление данных: то же + JSON `{url, confirmation_code}` |

`signed_request` проверяется по HMAC-SHA256 с `AppSecret`; невалидный — 400, ничего не удаляется.

Страницы — простой HTML на русском, пользовательские строки экранируются.

## Безопасность

- В режиме разработки приложения авторизоваться могут только аккаунты из Threads Testers,
  поэтому посторонний не сможет подменить аккаунт через `/threads/login`.
- `state` — случайный, одноразовый, живёт 15 минут (в памяти процесса).
- Токен нигде не отдаётся наружу и не пишется в логи.

## Конфигурация

Убираются `Threads__UserId` и `Threads__AccessToken`. Добавляются `Threads__AppId`,
`Threads__AppSecret`. `Threads__DryRun` остаётся; выключать его — после подключения аккаунта.

Если `DryRun=false`, а аккаунт не подключён, публикация падает с понятной ошибкой
(«Threads account is not connected: open …/threads/login») и уходит в повторы/`Failed`;
при старте пишется предупреждение в лог.

## Изменения в коде

- `ThreadsOptions`: `AppId`, `AppSecret` вместо `UserId`, `AccessToken`.
- `IThreadsTokenProvider` → `IThreadsAccountProvider` (`GetAccountAsync` → `ThreadsAccount(UserId, Username, AccessToken)`).
- `ThreadsAccountStore` — чтение/запись аккаунта в `settings`.
- `ThreadsTokenService` — берёт аккаунт из хранилища, продлевает токен; логику «seed из конфига» убрать.
- `ThreadsPublisher` — `UserId` и токен из `ThreadsAccount`.
- `ThreadsAuthClient` (HTTP к OAuth-эндпоинтам), `ThreadsConnector` (сценарий callback),
  `OAuthStateStore`, `SignedRequest`, `Api/ThreadsAuthEndpoints`.
- `.env.example`, `docs/deploy.md` (раздел 7 переписать под новую схему).

## Тесты

Без реальных вызовов: `ThreadsAuthClient` (URL авторизации, тела запросов, разбор
числового `user_id`), `ThreadsConnector` (успех, неверный state), `OAuthStateStore`
(одноразовость, истечение), `SignedRequest` (валидная/невалидная подпись),
обновлённые `ThreadsTokenService` и `ThreadsPublisher`.

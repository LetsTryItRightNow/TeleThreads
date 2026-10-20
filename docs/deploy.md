# Деплой TeleThreads

Сервис заменяет старый echo-сервер: это тот же проект и тот же контейнер `telethreads`,
поэтому отдельно удалять echo не нужно — `docker compose up -d --build` пересоздаст контейнер
с новым кодом. Домен меняется с `echo.dergach.xyz` на `telethreads.dergach.xyz` в Caddy.

Публикация в Threads пока в режиме **dry-run**: сервис всё обрабатывает и пишет в лог,
что отправил бы, но в Threads ничего не уходит.

## 0. Что подготовить заранее

**Telegram-бот**
1. В Telegram откройте [@BotFather](https://t.me/BotFather) → `/newbot` → сохраните токен.
2. Источник может быть каналом или группой, где пишут только администраторы.
   - **Канал:** добавьте бота администратором (настройки канала → Администраторы).
     Достаточно минимальных прав — бот только читает посты. Обычным участником канала
     бот посты не получает.
   - **Группа (супергруппа):** достаточно добавить бота участником. Он должен видеть все
     сообщения: в списке участников у него «has access to messages» (в @BotFather →
     Bot Settings → Group Privacy → Turn off). Публиковаться будут все сообщения группы
     с текстом или фото, поэтому участникам писать в группу должно быть запрещено.
   - Тип чата можно проверить: `curl "https://api.telegram.org/bot<ТОКЕН>/getChat?chat_id=<ID>"`
     → поле `type` (`channel` или `supergroup`).

**Gemini API key**
1. Откройте [Google AI Studio](https://aistudio.google.com/apikey) → *Create API key*.

## 1. Обновить код на сервере

```bash
ssh <ваш-сервер>
```

Если не помните, где лежит проект, это покажет docker:

```bash
docker inspect telethreads --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}'
```

Перейдите в этот каталог и заберите код:

```bash
cd <каталог проекта>
git checkout main
git pull
```

## 2. Создать `.env` с секретами

```bash
cp .env.example .env
nano .env
chmod 600 .env
```

Заполните `Telegram__BotToken` и `Gemini__ApiKey`. `Telegram__ChannelId` пока оставьте пустым —
узнаем его на шаге 5. `Threads__*` не трогайте, `Threads__DryRun=true`.

## 3. Пересобрать и запустить контейнер

```bash
docker compose up -d --build
docker compose ps          # STATUS должен быть Up
docker compose logs -f     # Ctrl+C — выйти из просмотра логов
docker image prune -f      # удалить старый образ echo
```

Проверка на самом сервере:

```bash
curl http://127.0.0.1:8088/health      # ok
curl http://127.0.0.1:8088/api/posts   # []
```

## 4. Переключить домен в Caddy

Найдите Caddyfile.
- Caddy установлен как сервис: `systemctl status caddy` → обычно `/etc/caddy/Caddyfile`.
- Caddy запущен в Docker: `docker ps` → найдите контейнер caddy,
  `docker inspect <контейнер caddy> --format '{{ json .Mounts }}'` покажет, откуда смонтирован Caddyfile.

В Caddyfile найдите блок для echo, примерно такой:

```
echo.dergach.xyz {
    reverse_proxy 127.0.0.1:8088
}
```

Поменяйте **только имя хоста** на `telethreads.dergach.xyz`. Строку `reverse_proxy` оставьте
как есть — она уже указывает на наш контейнер.

```
telethreads.dergach.xyz {
    reverse_proxy 127.0.0.1:8088
}
```

Проверьте конфиг и перезагрузите Caddy (без простоя):

```bash
# Caddy как сервис:
caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy

# Caddy в Docker:
docker exec <контейнер caddy> caddy validate --config /etc/caddy/Caddyfile
docker exec <контейнер caddy> caddy reload --config /etc/caddy/Caddyfile
```

Caddy сам выпустит сертификат для нового домена (DNS уже настроен). Проверка с любой машины:

```bash
curl https://telethreads.dergach.xyz/health   # ok
```

Если сразу не открывается — подождите минуту и посмотрите логи Caddy
(`journalctl -u caddy -n 50` или `docker logs <контейнер caddy>`).
Старый адрес `echo.dergach.xyz` перестанет отвечать; его DNS-запись можно удалить.

## 5. Узнать id канала и включить его

1. Опубликуйте в канале любой пост.
2. В логах появится строка вида:
   ```
   Ignoring post from chat -1001234567890 (Название канала); Telegram:ChannelId is '(null)'
   ```
   ```bash
   docker compose logs | grep "Ignoring post"
   ```
3. Впишите это число в `.env`: `Telegram__ChannelId=-1001234567890`.
4. Примените изменения (контейнер пересоздастся с новым `.env`):
   ```bash
   docker compose up -d
   ```
5. Удалите тестовый пост из канала, если он не нужен.

## 6. Проверить весь путь

Опубликуйте в канале пост с несколькими фото и текстом. В логах (`docker compose logs -f`) по порядку:

```
Saved post 1 from Telegram with 3 image(s)
Post 1 rewritten: 412 chars
[dry-run] Would publish to Threads as CAROUSEL: ... with 3 image(s): ...
Post 1 published to Threads as dry-run
```

Состояние постов: <https://telethreads.dergach.xyz/api/posts> (или `/api/posts/1`, `/api/posts?status=Failed`).
Откройте любую ссылку из `imageUrls` — фото должно открыться в браузере: именно по этим
ссылкам Threads будет забирать картинки.

Если пост в статусе `Failed` — причина в поле `lastError`.

## Полезные команды

```bash
docker compose logs -f --tail 100        # логи
docker compose restart                   # перезапуск
docker compose up -d --build             # после git pull
docker volume ls                         # том с БД и фото: telethreads-data
```

Резервная копия БД и фото:

```bash
docker compose stop    # чтобы не скопировать БД посреди записи
docker run --rm -v telethreads-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/telethreads-data.tgz -C /data .
docker compose start
```

## 7. Включение реальной публикации в Threads (позже)

Публикуем в аккаунт владельца без его пароля: он один раз открывает ссылку, входит в Threads
и нажимает «Разрешить» — сервис сам получает токен и хранит его в БД.

**Приложение Meta (делает разработчик)**

1. [Meta for Developers](https://developers.facebook.com/apps/) → *Create app* → сценарий
   *Access the Threads API*. Разрешения: `threads_basic`, `threads_content_publish`.
2. *Use cases → Access the Threads API → Settings* — заполните адреса и сохраните:
   - *Redirect Callback URLs*: `https://telethreads.dergach.xyz/threads/callback`
   - *Uninstall Callback URL*: `https://telethreads.dergach.xyz/threads/uninstall`
   - *Delete Callback URL*: `https://telethreads.dergach.xyz/threads/delete`
3. *App roles → Roles → Threads Testers* — добавьте имя пользователя Threads владельца аккаунта.
4. Владелец принимает приглашение в приложении Threads:
   Настройки → Аккаунт → Разрешения для сайтов → Приглашения.

**Сервер**

5. В `.env` впишите идентификатор и секрет приложения. У приложения их две пары: общие
   (*App settings → Basic*) и отдельные для Threads (в настройках сценария Threads API —
   *Threads app ID* и *Threads app secret*). Нужны **Threads**-шные:
   ```
   Threads__AppId=<Threads app ID>
   Threads__AppSecret=<Threads app secret>
   ```
   `Threads__DryRun=true` пока не трогайте. Примените: `docker compose up -d`.

**Подключение аккаунта**

6. Отправьте владельцу ссылку <https://telethreads.dergach.xyz/threads/login>. Он входит в Threads,
   нажимает «Разрешить» и видит «Готово! Аккаунт @… подключён».
7. Проверьте <https://telethreads.dergach.xyz/threads/status> — должно быть `"connected": true`
   и нужный `username`.
8. Включите публикацию: в `.env` `Threads__DryRun=false`, затем `docker compose up -d`.

Приложение Meta должно оставаться в режиме *Development* (подключиться могут только тестировщики) —
в *Live* его не переключайте.

Если владелец видит «Доступ не выдан» или ошибку при входе: проверьте, что он принял приглашение
тестировщика (шаг 4), и что *Redirect Callback URL* в Meta совпадает в точности с
`https://telethreads.dergach.xyz/threads/callback` (https, без слеша в конце).

Фото, сохранённые во время dry-run, автоматически не удаляются (удаляются только после
реальной публикации). Если хочется почистить: `docker exec telethreads sh -c 'rm -rf /data/media/*'`
перед включением реальной публикации.

Владелец может в любой момент отозвать доступ в настройках Threads (Разрешения для сайтов) —
Meta сообщит об этом сервису, и он забудет аккаунт.

Сервис сам продлевает токен раз в 7 дней — но только когда публикует посты. Если в канале
не будет постов дольше 60 дней, токен истечёт (посты начнут падать с ошибкой). Тогда просто
отправьте владельцу ссылку `/threads/login` ещё раз. Повторный вход заменяет сохранённый аккаунт.

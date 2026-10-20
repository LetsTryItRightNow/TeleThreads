# Деплой TeleThreads

Сервис заменяет старый echo-сервер: это тот же проект и тот же контейнер `telethreads`,
поэтому отдельно удалять echo не нужно — `docker compose up -d --build` пересоздаст контейнер
с новым кодом. Домен меняется с `echo.dergach.xyz` на `telethreads.dergach.xyz` в Caddy.

Публикация в Threads пока в режиме **dry-run**: сервис всё обрабатывает и пишет в лог,
что отправил бы, но в Threads ничего не уходит.

Посты и аккаунт Threads хранятся в MongoDB — это отдельный контейнер `mongo` в том же
`docker-compose.yml`, данные лежат в томе `telethreads-mongo`. Фото — в томе `telethreads-data`.
Если сервис уже установлен в версии с SQLite — см. раздел «Переход на MongoDB» ниже.

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

Задайте пользователя и пароль MongoDB (`MONGO_USER` можно оставить `telethreads`).
Пароль сгенерируйте так — в нём будут только буквы и цифры, экранировать ничего не придётся:

```bash
openssl rand -hex 16
```

```
MONGO_USER=telethreads
MONGO_PASSWORD=<результат openssl>
```

Пользователь создаётся при **первом** запуске контейнера `mongo` (пока том `telethreads-mongo`
пустой). Если потом поменять пароль в `.env`, в самой базе он не поменяется.

## 3. Пересобрать и запустить контейнер

```bash
docker compose up -d --build
docker compose ps          # telethreads — Up, mongo — Up (healthy)
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

## Тестовый пост без Telegram

Чтобы проверить Gemini и весь путь поста без публикации в Telegram, задайте в `.env`
секретный ключ (любая длинная случайная строка, например из `openssl rand -hex 16`):

```
Testing__Key=<ключ>
```

и выполните `docker compose up -d`. Затем на сервере создайте файл `post.json` (UTF-8):

```json
{
  "text": "Текст поста, как он был бы в Telegram",
  "imageUrls": ["https://example.com/photo1.jpg", "https://example.com/photo2.jpg"]
}
```

`imageUrls` можно не указывать. Отправьте:

```bash
curl -X POST http://127.0.0.1:8088/api/test/posts \
  -H "X-Test-Key: <ключ>" -H "Content-Type: application/json" \
  --data-binary @post.json
```

Ответ — `{"id":5,...}`. Через несколько секунд на `/api/posts/5` появится `rewrittenText`
от Gemini, а в логах — строка `[dry-run] Would publish ...`.

Тестовый пост идёт по тому же пути, что и настоящий: при `Threads__DryRun=false` он
**будет опубликован в Threads**. Без ключа (`Testing__Key` пустой) эндпоинт отвечает 404.

## Полезные команды

```bash
docker compose logs -f --tail 100        # логи
docker compose restart                   # перезапуск
docker compose up -d --build             # после git pull
docker volume ls                         # тома: telethreads-mongo (БД) и telethreads-data (фото)
```

## Переход на MongoDB (если сервис уже работал с SQLite)

Данные из SQLite **не переносятся** (там были только тестовые посты): после обновления
список постов начнётся заново с id 1, а аккаунт Threads, если он был подключён,
нужно будет подключить ещё раз через `/threads/login`.

1. До обновления добавьте в `.env` пользователя и пароль MongoDB (см. шаг 2):
   ```bash
   echo "MONGO_USER=telethreads" >> .env
   echo "MONGO_PASSWORD=$(openssl rand -hex 16)" >> .env
   ```
2. Заберите код и пересоберите — compose поднимет и контейнер `mongo`:
   ```bash
   git pull
   docker compose up -d --build
   docker compose ps          # telethreads — Up, mongo — Up (healthy)
   curl http://127.0.0.1:8088/api/posts   # []
   ```
3. Старый файл SQLite больше не нужен, его можно удалить:
   ```bash
   docker exec telethreads sh -c 'rm -f /data/telethreads.db*'
   ```

## База данных (MongoDB)

Порт MongoDB открыт только на самом сервере (`127.0.0.1:27017`), снаружи он недоступен.

**MongoDB Compass со своего компьютера** — через SSH-туннель:

```bash
ssh -L 27017:127.0.0.1:27017 <ваш-сервер>
```

Пока это соединение открыто, в Compass подключитесь по строке:

```
mongodb://<MONGO_USER>:<MONGO_PASSWORD>@localhost:27017/?authSource=admin
```

База — `telethreads`, коллекции: `posts` (посты), `counters` (счётчик id постов),
`settings` (аккаунт и токен Threads). Если на вашем компьютере порт 27017 уже занят
(например, локальной MongoDB), пробросьте другой: `ssh -L 27018:127.0.0.1:27017 <ваш-сервер>`
и в строке подключения укажите `localhost:27018`.

**mongosh на сервере** (из каталога проекта):

```bash
export $(grep '^MONGO_' .env)   # взять MONGO_USER и MONGO_PASSWORD из .env
docker compose exec mongo mongosh -u "$MONGO_USER" -p "$MONGO_PASSWORD" --authenticationDatabase admin telethreads
```

Примеры запросов внутри mongosh:

```js
db.posts.find().sort({_id: -1}).limit(5)        // последние 5 постов
db.posts.find({status: "Failed"}, {lastError: 1}) // упавшие посты и причина
db.posts.countDocuments({status: "Published"})   // сколько опубликовано
```

Выход — `exit`.

**Резервная копия** базы и фото:

```bash
export $(grep '^MONGO_' .env)
docker compose exec -T mongo mongodump -u "$MONGO_USER" -p "$MONGO_PASSWORD" \
  --authenticationDatabase admin --db telethreads --archive > telethreads-mongo.archive
docker run --rm -v telethreads-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/telethreads-media.tgz -C /data media
```

`mongodump` работает на ходу, останавливать сервис не нужно. Восстановление базы из копии:

```bash
export $(grep '^MONGO_' .env)
docker compose exec -T mongo mongorestore -u "$MONGO_USER" -p "$MONGO_PASSWORD" \
  --authenticationDatabase admin --drop --archive < telethreads-mongo.archive
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

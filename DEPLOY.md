# Запуск сервера в интернете

Нужны: VPS на Linux (1 ядро, 1–2 ГБ памяти, Ubuntu 24.04) и домен.

## 1. Домен

В панели регистратора создайте запись типа **A**: имя — `@` или поддомен, значение — IP-адрес вашего VPS.
Запись начинает работать в течение нескольких минут или часов.

## 2. Сервер

Зайдите на VPS по SSH. Если памяти 2 ГБ и меньше, добавьте файл подкачки, чтобы хватило на сборку:

```bash
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile && echo '/swapfile none swap sw 0 0' >> /etc/fstab
```

Поставьте Docker:

```bash
curl -fsSL https://get.docker.com | sh
```

Откройте наружу только нужные порты (22 — для SSH, 80 и 443 — для сайта):

```bash
ufw allow 22/tcp && ufw allow 80/tcp && ufw allow 443/tcp && ufw enable
```

Убедившись, что вход по SSH-ключу работает, отключите вход по паролю:

```bash
printf 'PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
' > /etc/ssh/sshd_config.d/00-ets-hardening.conf && sshd -t && systemctl reload ssh
```

## 3. Запуск

Код передаётся с вашего компьютера через SSH: серверу не нужен доступ к приватному репозиторию.
Выполните в папке проекта на своём компьютере (подставьте IP сервера):

```bash
git archive --format=tar HEAD | ssh root@IP "mkdir -p /opt/ets && tar -x -C /opt/ets"
```

Дальше на сервере:

```bash
cd /opt/ets && cp .env.example .env && nano .env
```

В `.env` укажите домен и пароль базы. Пароль можно получить командой `openssl rand -hex 32`.

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

Код первого администратора:

```bash
docker compose -f docker-compose.prod.yml logs server | grep администратора
```

Откройте `https://ваш-домен`. Страница рейтинга должна открыться с замком в адресной строке.

## 4. Подключение

### Как стать первым администратором

Первый администратор получает роль на сервере, по одноразовому коду из журнала сервера. Установка
трекера роль не даёт. Сервер показывает код при каждом запуске, пока на нём нет ни одного водителя.

1. На сервере посмотрите код:

   ```bash
   cd /opt/ets && docker compose -f docker-compose.prod.yml logs server | grep администратора
   ```

   Если строки нет, а водителей на сервере ещё нет, перезапустите сервер и посмотрите снова:

   ```bash
   cd /opt/ets && docker compose -f docker-compose.prod.yml restart server
   ```

2. Запустите трекер и зарегистрируйте водителя на компьютере или выберите уже созданного.
3. На вкладке «Рейтинг» укажите адрес `https://ваш-домен` полностью, вместе с `https://`. Если вы уже
   вошли на другой сервер, сначала нажмите «Выйти».
4. Введите код, ник и новый пароль, затем нажмите «Зарегистрироваться». Трекер предложит отправить
   записанные рейсы на сервер.
5. В шапке появятся ★ и кнопка «Водители». При следующем выборе водителя трекер попросит придумать
   PIN-код.
6. Нажмите «Водители» и задайте себе ранг и допуски.

Код одноразовый. Никому его не пересылайте: зарегистрировавшийся по нему становится администратором.

### Другие водители и администраторы

Создайте код приглашения на вкладке «Рейтинг» и отправьте его водителю. Чтобы сделать водителя
администратором, откройте «Водители», выберите его, отметьте «Администратор» и нажмите «Сохранить».
Он должен быть уже зарегистрирован на сервере.

Если войти под администратором больше нельзя, код из журнала не поможет: водители на сервере уже есть.
Роль можно выдать другому зарегистрированному водителю прямо в базе (подставьте его ник):

```bash
cd /opt/ets && docker compose -f docker-compose.prod.yml exec -T db psql -U ets -d ets -c "UPDATE drivers SET is_admin = true WHERE nickname = 'ник'"
```

## Обновление

С вашего компьютера, из папки проекта:

```bash
git archive --format=tar HEAD | ssh root@IP "tar -x -C /opt/ets && cd /opt/ets && docker compose -f docker-compose.prod.yml up -d --build"
```

## Настройки компании

Ставки и название лежат на сервере, в `/data` контейнера. После правки перезапустите сервер:

```bash
docker compose -f docker-compose.prod.yml exec server sh -c 'cat /data/company.json'
```

```bash
docker compose -f docker-compose.prod.yml restart server
```

## Резервные копии

Раз в сутки копия базы попадает в папку `backups` и хранится 14 дней. Копии лежат на том же сервере:
если он пропадёт целиком, пропадут и они, поэтому иногда скачивайте их к себе.

Восстановление из копии **заменяет текущие данные**:

```bash
docker compose -f docker-compose.prod.yml stop server
```

```bash
gunzip -c backups/ets-ГГГГ-ММ-ДД.sql.gz | docker compose -f docker-compose.prod.yml exec -T db sh -c 'psql -U ets -d postgres -c "DROP DATABASE ets" -c "CREATE DATABASE ets" && psql -U ets -d ets'
```

```bash
docker compose -f docker-compose.prod.yml start server
```

## Важно

- `docker compose ... down -v` **удаляет все данные** сервера. Без `-v` данные сохраняются.
- Не отдавайте и не выкладывайте файл `.env`.

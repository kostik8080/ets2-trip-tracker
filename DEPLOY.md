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

В трекере: вкладка «Рейтинг» → «Выйти» → адрес `https://ваш-домен` → код первого администратора →
«Зарегистрироваться». Трекер предложит отправить ваши рейсы на новый сервер. Друзьям отправьте коды
приглашений с той же вкладки.

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

# Deploy

Результаты проверки 2026-11-05: [функциональные исправления, автоматические тесты, Android-отчёты и сохранность данных](hour-review-20261105.md). Bash-проверка включает сценарий сохранения мобильных PDF/XLSX так же, как PowerShell-проверка.

Инфраструктура локального запуска проекта.

## Деплой из Bash

Из корня проекта на машине с работающим Docker Compose и `curl`:

```bash
./deploy/deploy.sh up
./deploy/deploy.sh check
./deploy/deploy.sh status
./deploy/deploy.sh logs
./deploy/deploy.sh stop
```

`up` подходит для первого запуска и обновления. Он собирает API и web, запускает PostgreSQL и Redis, дожидается готовности контейнеров и проверяет API и веб-панель через опубликованные порты. API применяет миграции при старте. `stop` сохраняет тома PostgreSQL, Redis и отчётов.

Для запуска с уже собранными образами используйте `./deploy/deploy.sh --skip-build up`. Переменные Compose можно задать в `deploy/.env` по образцу `deploy/.env.example` или передать файл через `--env-file`. Для отдельного стенда укажите имя проекта и его Compose override:

```bash
./deploy/deploy.sh --project-name asset-demo --override deploy/docker-compose.demo.yml up
./deploy/deploy.sh --project-name asset-demo --override deploy/docker-compose.demo.yml stop
```

Этот запуск использует локальные порты, заданные Compose. Node и .NET SDK на хосте нужны только для разработки вне контейнеров.

### Локальная разработка из Bash

Из корня проекта, после установки зависимостей web/mobile:

```bash
bash deploy/start-local.sh
bash deploy/start-local.sh --skip-build
bash deploy/check-local.sh --health-only
bash deploy/check-local.sh
```

Первый запуск собирает контейнеры; `--skip-build` использует существующие образы.
Compose перезапускает сервисы после сбоя процесса и при возвращении Docker Engine. Явно остановленный контейнер остаётся остановленным до ручного запуска.
Проверка без `--health-only` запускает backend, web и mobile тесты, включая скачивание мобильных отчётов в Chrome. Нужны .NET SDK 10, Node >=20.19.4 с npm, Docker, curl и установленный Chrome (либо `PLAYWRIGHT_CHROME_PATH`). Прогон 2026-11-05 выполнен с SDK 10.0.401 и Node 24.22.1. Рабочие volumes не удаляются, пароли не сбрасываются.

На этой Windows-машине Docker Desktop использует контекст `desktop-linux`:

```bash
DOCKER_CONTEXT=desktop-linux bash deploy/start-local.sh --skip-build
```

Полный Testcontainers-прогон в Windows дополнительно использует `DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine`; на Linux с обычным Docker Engine этот override не нужен. Прямой вызов через `bash` работает без изменения executable bit скриптов.

```bash
# Только для указанного Windows Docker Desktop:
DOCKER_CONTEXT=desktop-linux \
DOCKER_HOST=npipe://./pipe/dockerDesktopLinuxEngine \
bash deploy/check-local.sh
```

PostgreSQL и Redis не публикуют порты на host и доступны только сервисам в сети
`asset-inventory-backend`. При первом запуске создается администратор с логином из
`ADMIN_LOGIN` (по умолчанию `admin`) без пароля; пароль задается через web-панель
при первом входе.

API опубликован только на `127.0.0.1:5147`, web — на `127.0.0.1:5173`. Анонимный HR endpoint доступен с host
или из доверенной Docker-сети; web reverse proxy явно запрещает `/internal`.
Nginx заново разрешает адрес сервиса `api` через Docker DNS после пересоздания контейнера. Для импорта прокси допускает multipart до 21 МБ с учётом служебных полей. API ограничивает сам файл 20 МБ.
Не заменяйте loopback-привязку на публичный интерфейс без отдельной сетевой защиты.
Изменение привязки порта применяется при пересоздании соответствующего контейнера.

Запуск из корня проекта (PowerShell 7):

```powershell
docker compose -f deploy/docker-compose.yml up -d --build
```

Web: `http://localhost:5173`, readiness: `http://localhost:5147/health/ready`.
Readiness проверяет PostgreSQL, Redis и возможность записи/чтения файлов отчетов.

## Единый локальный запуск и проверки

```powershell
./deploy/start-local.ps1
# Без пересборки контейнеров:
./deploy/start-local.ps1 -SkipBuild
./deploy/check-local.ps1
# Только доступность трёх сервисов:
./deploy/check-local.ps1 -HealthOnly
```

Нужны запущенный Docker Desktop, .NET SDK из backend/global.json, Node/npm,
установленные зависимости web/mobile и Chrome для Playwright.
Start запускает API/web/PostgreSQL/Redis и mobile web на 5174, ждёт readiness.
Повторно использует существующий mobile web, не убивает чужой процесс при
занятом порту. При новом запуске Vite логи находятся в tmp/local-mvp.
Данные/отчёты в volumes сохраняются; init/reset, новый пароль и Android запуск
не выполняются. Check завершается ошибкой при первом неуспешном тесте/сборке,
проверяет backend, web unit/build/mock Playwright, mobile Jest/typecheck/lint/build.
Обычный check не изменяет рабочие бизнес-данные и не запускает live-тесты.

## Проверка пагинации текущей БД без изменения бизнес-данных

`verify-pagination.ps1` проверяет страницы состава/QR, глобальные счётчики, поиск,
справочники, расписания, события, историю назначений и доступ назначенного worker.
Он не создаёт/изменяет ОС или проверки; создаёт отдельные сеансы входа и отзывает их
в конце. API должен быть loopback. Для воспроизводимости выбирайте закрытую проверку.

```powershell
$paginationCredential = Get-Credential -UserName admin
./deploy/verify-pagination.ps1 -AdminLogin $paginationCredential.UserName -AdminPassword $paginationCredential.GetNetworkCredential().Password -InventoryId '01a10624-3a80-7ce2-82f6-2b643bc0c9cc'
```

По умолчанию используется worker2 из локального fixture; для другой проверки
задайте соответствующие `WorkerLogin`/`WorkerPassword`, либо пустой `WorkerLogin`
для пропуска мобильной проверки. Пароль администратора в проект не сохраняется.

## Резервная копия и восстановление

```powershell
./deploy/backup.ps1 -ProjectName deploy
./deploy/verify-backup.ps1 -BackupPath ./backups/ИМЯ-КОПИИ
# Заменяет выбранную БД; сначала сделайте актуальную копию!
./deploy/restore.ps1 -ProjectName deploy -BackupPath ./backups/ИМЯ-КОПИИ -ConfirmReplaceDatabase
```

Копия содержит custom-format pg_dump, каталог документов и SHA-256 manifest.
При копировании API кратко приостанавливается, чтобы БД и файлы соответствовали
друг другу. Проверка сверяет состав файлов с манифестом и восстанавливает БД в отдельном временном PostgreSQL 17,
не публикуя порт. Восстановление проверяет временную копию, затем останавливает API,
заменяет БД в одной транзакции PostgreSQL и сверяет документы после копирования. При ошибке API остается остановленным, чтобы
не публиковать частично восстановленное состояние. Старые файлы без ссылок из БД
не удаляются. Перед запуском API скрипт очищает Redis выбранного Compose-проекта и отзывает выданные токены. После восстановления пользователи входят заново. Для восстановления PostgreSQL и Redis проекта должны быть запущены, а API должен использовать актуальный образ для применения миграций старых копий.
Копии содержат персональные данные и хеши токенов: храните их вне Git,
с ограниченным доступом; копируйте на отдельный носитель.

## Изолированная демонстрация и приемка

```powershell
docker compose -p asset-demo -f deploy/docker-compose.yml -f deploy/docker-compose.demo.yml up -d --no-build
./deploy/demo.ps1 -Password 'Задайте-демо-пароль'
./deploy/acceptance.ps1 -Password 'Тот-же-демо-пароль'
```

Сначала соберите основной стек. Demo использует отдельные volumes, сеть и порты
15147/15173. Seed отказывается перезаписывать заполненные справочники и работать
на обычном порту 5147. Создает организацию, отделы, помещение, МОЛ,
`demo.inventory` и импортирует три ОС из `demo-assets.csv` через dry-run/apply.
Пароль явно задается при запуске и не включается в образ.
Acceptance изменяет только демоданные, проходит полный и частичный обход,
подтверждения, повтор HR-события с часовым смещением, автозапуск событийной проверки и неизменяемость отчета.
По умолчанию сохраняет артефакты в `tmp/acceptance`. Параметр `-OutputDirectory` позволяет выбрать отдельную папку для нового прогона. Скрипт оставляет активную проверку и `qr.png` для [демонстрации через OBS](../mobile/README.md#демонстрация-через-obs-на-одном-пк).

Для Android debug через Metro:

```powershell
cd mobile
$env:ASSET_DEBUG_API_URL = 'http://10.0.2.2:15147/api/v1'
npm start -- --reset-cache
```

После смены URL перезапустите Metro с очисткой cache. Без переменной debug
использует `10.0.2.2:5147`. `ASSET_RELEASE_API_URL` встраивается при сборке;
разрешен только HTTPS. Технический release по-прежнему использует debug-подпись
и placeholder URL при отсутствии переменной: это не пакет для распространения.

Для локальной проверки сохранения скана после редизайна:

```powershell
$credentials = Get-Credential -UserName admin -Message 'Пароль существующего локального администратора'
./deploy/verify-scan-workflow.ps1 -InventoryId <UUID-свежего-клона-INV-MVP-002> -AdminPassword $credentials.GetNetworkCredential().Password
```

В отличие от read-only проверки пагинации, этот скрипт запускает тестовый клон
и сохраняет MVP-0001 Found/Damaged. Допускает только loopback API и клон с
источником INV-MVP-002, не закрывает обход и не меняет исходную Completed.
Повторный запуск требует свежего клона, назначенного mvp.worker2, с Pending
MVP-0001. Собственные сеансы закрываются; UI-сеансы не отзываются.
Это проверка HTTP-сценария, не ручная работа камерой или в интерфейсе.

## Живой браузерный тест мобильного web (локальный MVP)

Из каталога `web`, при уже запущенных API 5147 и mobile Vite 5174:

```powershell
$env:MOBILE_TEST_INVENTORY = '<UUID-активного-тестового-клона-INV-MVP-002>'
npm run test:e2e:mobile-live
```

Сценарий скана изменяет одно Pending ОС с номером MVP-* в указанном клоне,
проверяя исчезновение из remaining и сохранение повреждения/комментария в found.
Второй сценарий только читает UUID-поиск; его можно запустить отдельно:
`npm run test:e2e:mobile-live -- --grep UUID`.
Без явного UUID сценарии записи пропускаются. Клон должен быть Active и назначен
fixture-пользователю mvp.worker2; источник проверяется до записи. Каждый запуск
скана выбирает следующее Pending ОС: повторно запускайте осознанно.
Нужен локальный Google Chrome (или PLAYWRIGHT_CHROME_PATH).
Обычный `npm run test:e2e` использует mock API и этих тестов не запускает.
Тесты не используют пользовательскую вкладку и закрывают свои login-сеансы;
trace/video/screenshots отключены, пароли/токены не выводятся в stdout.

Для полного пакета с отклонением, повторным обходом и подтверждением подготовьте
новый клон (команда создаёт синтетическую бизнес-запись в текущей БД):

```powershell
# Из корня проекта:
$liveCredential = Get-Credential -UserName admin
$env:MOBILE_TEST_INVENTORY = ./deploy/prepare-mobile-test.ps1 -AdminPassword $liveCredential.GetNetworkCredential().Password
$env:MOBILE_TEST_ADMIN_PASSWORD = $liveCredential.GetNetworkCredential().Password
try {
    Push-Location web
    try { npm run test:e2e:mobile-live } finally { Pop-Location }
} finally {
    Remove-Item Env:MOBILE_TEST_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:MOBILE_TEST_INVENTORY -ErrorAction SilentlyContinue
}
```

Все шесть mobile live-тестов проверены. Дополнительные сценарии инъецируют отказ
транспорта, challenge-401 и 503 в отдельный браузер, не останавливая общий API.
Проверяются сохранность комментария, refresh/retry без двойного результата,
восстановление списка и status-фильтры, полный цикл reject/start/scan/confirm,
скачивание обоих отчётов и неизменность исходной INV-MVP-002.
Отдельный read-only сценарий отвергает refresh, проверяет возврат к логину
и отсутствие прежних проверок в UI. Он не требует UUID.
Полный сценарий оставляет свой клон Completed (1 Found/199 NotFound); для
повторного полного прогона нужен новый клон. API availability test только читает
данные и может выполняться без UUID. Артефакты live — tmp/playwright-mobile-live,
отдельно от обычного web/test-results. Пароль не записывается в проект.
DOM failure-контексты также удаляются (`preserveOutput: never`), поскольку
они могут содержать значение password input. Read-only UUID-сценарий выбирает
«Все» и доступен также для Completed тестового клона.

### Настоящая административная UI-приёмка

В `web/tests/live/admin.spec.ts` есть read-only обход административных экранов,
21-я страница реестра, поиск/карточка/PNG download и ошибки пустой формы/импорта.
Использует настоящий Chrome и текущий API, не mock. Передавайте существующий
пароль через `MOBILE_TEST_ADMIN_PASSWORD`, как выше; запустите из `web`:

```powershell
npm run test:e2e:mobile-live -- admin.spec.ts --grep 'real admin pages'
```

Второй opt-in тест требует также `NATIVE_TEST_RESTART_INVENTORY` — UUID явно
выбранного тестового клона с источником INV-MVP-002. Он повторно запускает
ReviewRequired через web UI (или проверяет уже выполненный незавершённым
прогоном restart), ожидает 200 Pending/0 Found и проверяет исходные отчёты.
Не запускайте его на рабочей проверке или клоне с результатами. Оба сценария
прошли 04.10.2026; собственные cookie-сеансы закрываются с проверкой HTTP 204.
Полный CRUD справочников и применение импорта этим тестом не покрыты.

Отдельный `web/tests/live/assets-write.spec.ts` проверяет настоящий CRUD ОС,
конфликт двух вкладок, CSV с 100 ошибками/пагинацией/выгрузкой, dry-run/apply/upsert,
quoted multiline UTF-8 и архивирование. Для него дополнительно задайте
`ADMIN_TEST_ALLOW_WRITES=1` и выполните из `web`:

```powershell
npm run typecheck:live
npm run test:e2e:mobile-live -- assets-write.spec.ts
```

После прогона удалите обе переменные `MOBILE_TEST_ADMIN_PASSWORD` и
`ADMIN_TEST_ALLOW_WRITES` в finally. Тест создаёт два новых ОС QA-LIVE-<run>-FORM/CSV
и архивирует только свои UUID (API-cleanup при сбое, UI-архив при нормальном ходе).
Архивные записи/аудит сохраняются; при каждом прогоне общее количество ОС
увеличивается на два, активный реестр возвращается к исходному количеству.
Не запускайте live-записи на production. XLSX apply/CRUD справочников не покрыты.
`check-local.ps1` проверяет типы всех live-сценариев, но не исполняет их и не
читает пароли; для записи нужен отдельный явно разрешённый запуск.

### Актуальная нативная Android-проверка

Основной цикл проверен на AssetInventory_API34 через настоящий debug APK,
ADB/UIAutomator и текущий API `http://10.0.2.2:5147/api/v1` (не demo 15147).
Для Metro задайте этот адрес перед `npm start -- --reset-cache`. Сборка в
`mobile/android`: `./gradlew.bat assembleDebug -PreactNativeArchitectures=x86_64`
с настроенными JAVA_HOME/ANDROID_HOME; установка `adb install -r` сохраняет данные.
При чёрных screenshots старого AVD snapshot помог холодный запуск без очистки
данных: `emulator -avd AssetInventory_API34 -no-snapshot -gpu angle_indirect`.
Системные разрешения/настройки и видеопоток AVD проверены; оптический QR,
автофокус и аппаратный фон физического телефона ещё не проверены.
Актуальные UUID/результаты: `manual-test-report-20261004.md`.

## Production

Текущая цель — локальный MVP; Development-ключи и HTTP не предназначены для LAN/Интернета.
Публичное облако не входит в MVP. Перед реальной эксплуатацией задайте
`ASPNETCORE_ENVIRONMENT=Production`, собственный `Jwt__SigningKey` (не менее
32 символов), пароль PostgreSQL и точные `Cors__Origins__0` через environment/secrets.
Запуск вне Development проверяет эти значения и отвергает дефолтные JWT/пароль БД,
HTTP/wildcard CORS. Refresh cookie всегда Secure вне Development. Для внешнего TLS
proxy задайте конкретные `ReverseProxy__KnownProxies__0` (и последующие IP);
без этого forwarded-заголовки недоверенного proxy игнорируются. Никогда не доверяйте всем адресам.
Настройте HTTPS на внешнем reverse proxy и не публикуйте
API/internal в недоверенную сеть. Для Android нужны URL сервера и отдельный
production signing keystore. `.env`, ключи и резервные копии исключены из Git.

Файлы инфраструктуры:

- `docker-compose.yml`;
- Dockerfile backend-сервиса;
- Dockerfile административной веб-панели;
- конфигурация reverse proxy;
- примеры переменных окружения;
- инструкции резервного копирования PostgreSQL и файлов отчетов.

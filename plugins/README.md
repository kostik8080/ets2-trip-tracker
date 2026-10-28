# Плагин телеметрии

`scs-telemetry.dll` — плагин из проекта [RenCloud/scs-sdk-plugin](https://github.com/RenCloud/scs-sdk-plugin)
(лицензия MIT, см. `src/SCSSdkClient/LICENSE-scs-sdk-plugin.txt`), 64-битная сборка.

Он встраивается в `EtsTracker.App` при сборке, и трекер сам ставит его в папку игры
(`Euro Truck Simulator 2\bin\win_x64\plugins`) по кнопке «Установить плагин».
Чтобы обновить плагин, замените этот файл и пересоберите трекер.

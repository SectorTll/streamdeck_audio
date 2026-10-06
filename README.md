# Audio Keys — Stream Deck plugin

Кнопки выбора устройства вывода звука для Elgato Stream Deck (Windows).
Каждая кнопка — одно устройство. Полоска-светодиод сверху показывает состояние:

| Состояние | Что значит | По умолчанию |
|---|---|---|
| selected | устройство выбрано по умолчанию в Windows | зелёный, горит |
| present | подключено, но не выбрано | зелёный, погашен |
| absent | в системе нет | жёлтый, горит (или кнопка скрыта) |

Цвета для каждого состояния задаются в настройках кнопки. Мигания нет.

## Как устроено

- `src/` — плагин на C# (.NET 8, framework-dependent, нужен установленный рантайм
  `Microsoft.WindowsDesktop.App 8`). Один процесс, без вспомогательных exe.
- Слежение за устройствами — через события Core Audio (`IMMNotificationClient`),
  опроса нет, процессор в покое не используется.
- Переключение — `IPolicyConfig::SetDefaultEndpoint` (console + multimedia, по желанию
  communications).
- Картинки 144×144 рисуются `System.Drawing` при изменении состояния и кешируются как
  готовые data-URI. Повторное состояние — поиск в словаре, без рендера.
- Устройство привязано по ID, при смене ID (переустановка драйвера) ищется по имени и
  перепривязывается автоматически.
- `plugin/` — манифест, панель настроек (`ui/`, на официальных sdpi-components v4),
  иконки генерируются сборкой.

## Сборка и установка

```powershell
.\build.ps1          # publish → иконки → копирование в %APPDATA%\Elgato\StreamDeck\Plugins → рестарт Stream Deck
.\build.ps1 -NoRestart
```

Лог плагина: `%APPDATA%\Elgato\StreamDeck\Plugins\com.deniss.audiokeys.sdPlugin\logs\audiokeys.log`.

## Настройки кнопки

`deviceId`, `deviceName`, `glyph` (headphones, headset, speaker, speakers, monitor, vr, usb,
bluetooth, none), `colorActive`, `colorInactive`, `colorAbsent`, `absentMode` (led | hidden),
`setCommunications`, `showLabel`.

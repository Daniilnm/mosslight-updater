# Интерфейс Mosslight

WPF, настоящее окно приложения. Тёмный фон #111514, поверхности #1C221E, зелёная объёмная кнопка, Segoe UI и Consolas, отдельная панель настроек и восстановления. Главный экран показывает состояние, объём загрузки и выбранную папку. Кнопки имеют состояния наведения, нажатия, клавиатурного фокуса и недоступности; ошибки объясняются обычным текстом.

| До | После |
|---|---|
| Полный ZIP и ручное копирование | Одно действие «Обновить» после первого выбора папки |
| Ручное удаление старых модов | Учёт файлов, автоматическое удаление с бэкапом |
| Обновление программы отдельной загрузкой | Баннер «Обновить программу», перезапуск и автоматический возврат старого EXE |
| Общая инструкция в TXT | Состояние и необходимое действие прямо в окне |

Фон — оригинальная иллюстрация кубического мира с мастерской, сделанная через **встроенный imagegen**, без API-ключа и CLI. Рабочая копия: `App/Assets/hero.png`. Изображение используется как фон настоящего интерфейса, кнопки и текст нарисованы WPF.

Исходный результат генерации сохранён встроенным инструментом в `$CODEX_HOME/generated_images/01a13f53-5bc8-7101-bc2c-81bb36d3daf5/exec-d0d8a1f8-9fde-4bb9-939a-2fa4ae661468.png`, затем скопирован в проект. Модель вручную не выбиралась, использован штатный режим встроенного инструмента.

## Промпт RU

```text
Создай оригинальный панорамный фон для настольного обновлятора модпака в стиле Minecraft. Кубический мир с чёткой геометрией блоков: замшелые каменные утёсы, ступенчатый водопад, ели, небольшая инженерная мастерская из камня и дерева с тёплыми янтарными окнами и скромным латунным водяным колесом. Спокойная горная долина в синий час, слои тумана между далёкими блочными вершинами, маленькие фонари у мастерской. Полированная игровая иллюстрация voxel key art, сдержанные материалы и кинематографическое освещение: прохладное бирюзовое небо, рассеянный синий свет, тёплый янтарный свет мастерской, глубокая лесная зелень. Широкий пейзаж, взгляд немного сверху; главный сюжет справа, слева и снизу слева темнее и спокойнее для настоящего белого текста интерфейса. Только фоновая иллюстрация, без текста, букв, цифр, логотипов, водяных знаков, интерфейса, рамок, персонажей и кнопок.
```

## Отправленный промпт EN

```text
Create an original panoramic background illustration for a Minecraft-style modpack updater desktop launcher. Subject: a voxel world built entirely from crisp cubic blocks, with mossy stone cliffs, a stepped waterfall, spruce trees, and one small stone-and-wood engineering workshop with warm amber windows and a modest brass waterwheel. Setting: a peaceful mountain valley at blue hour, mist layers between distant blocky peaks, tiny warm lanterns near the workshop. Style: polished stylized voxel game key art, precise block geometry and rich but restrained materials, cinematic environmental illustration. Lighting: cool teal evening sky and diffuse blue ambient light, warm amber light around the workshop, deep forest greens. Composition: a wide landscape seen from a slightly elevated viewpoint; place the strongest visual interest toward the right half; keep the left and lower-left calmer and darker so real white interface text can later be overlaid. The image is the background artwork only, not an interface screenshot. Empty sky and landscape contain no text, letters, numbers, logos, watermarks, UI elements, borders, characters, or buttons.
```

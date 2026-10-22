namespace FormatWheel;

internal sealed partial class Strings
{
    public static Strings Ru => new()
    {
        TrayAutostart = "Запускать при входе в Windows",
        TrayOpenFolder = "Открыть папку с копиями",
        TrayOpenLog = "Открыть журнал",
        TrayExit = "Выход",
        TrayExtensions = "Расширения",
        TrayOpenExtensions = "Открыть папку расширений",
        TrayReloadExtensions = "Перезагрузить расширения",
        NoExtensions = "Расширения не установлены",
        ExtensionsReloaded = n => $"Загружено расширений: {n}",

        AlreadyRunning = "Format Wheel уже запущен — иконка в трее.",
        EngineLoadFailed = "Не удалось загрузить модуль конвертации:",
        ElevatedTitle = "Запущено от администратора",
        ElevatedText = "Windows не даёт перетаскивать файлы из обычного Проводника в такие окна. Запустите без прав администратора.",
        RunningTitle = "Format Wheel работает",
        RunningText = "Тяните файл и зажмите Shift. Добавьте Ctrl или Alt для других меню. Отпустите Shift на нужном.",
        HeicMissingText = "Для записи HEIC установите из Microsoft Store «Расширения для изображений HEIF» и «Расширения для видео HEVC», затем перезапустите программу.",
        AutostartFailed = "Не удалось изменить автозапуск",
        ErrorTitle = "Format Wheel: ошибка",
        ConvertFailedTitle = "Не удалось сконвертировать",
        OriginalDragged = "Перетащен оригинал.",
        GrabFailed = "Не удалось перехватить перетаскивание. Подробности — в журнале (меню в трее).",
        JobSavingTo = folder => $"Доделываю → {folder}",
        JobSavedIn = folder => $"Готово, лежит в «{folder}» · показать",
        Desktop = "Рабочий стол",
        JobCancel = "Отменить конвертацию",
        JobCancelling = "Отменяю…",

        DragTips = new[]
        {
            "Если что, я тут 👋",
            "Если что — ты знаешь, как меня позвать",
            "Пс-с, могу перекрутить этот файл",
            "Не тот формат? Я рядом",
            "Колесо наготове",
            "Тащишь? Могу и в другом формате",
            "Я всё вижу 👀",
            "Помочь с форматом?",
            "Просто напоминаю: я тут",
            "Зажми Shift — покручу",
        },
        DragTipText = "Зажми Shift, пока тянешь. Нажми на это сообщение — и я больше не буду напоминать.",
        DragTipsOffTitle = "Ладно, молчу 🤐",
        DragTipsOffText = "Больше не напоминаю. Колесо всё так же открывается по Shift.",

        PickOne = "выберите",
        EmptySlot = "здесь ничего нет",
        SizeUnknown = "не удалось прочитать размер",
        TooSmall = dims => $"{dims} — уменьшать некуда",
        FileCount = FilesRu,

        Converting = "Конвертация...",
        Done = "Готово",
        Failed = "Ошибка",

        SizeSlot = "Размер",
        Original = "Оригинал",
        Unchanged = "без изменений",
        LongSide = "длинная сторона",
        EachFile = "от каждого файла",
        OwnRatio = "свои пропорции",
        MixedRatios = "разные пропорции",
        RatioOf = ratio => $"пропорции {ratio}",
        Megapixels = mp => $"{mp} Мп",
        SizeLarge = "большой",
        SizeMedium = "средний",
        SizeAvatar = "аватар",
        SizeIcon = "иконка",
        SizeEmail = "для почты",
        SizePreview = "превью",
        SizeWeb = "для сайтов",
        SizePrint = "печать 10×15",

        HintJpg = "везде",
        HintPng = "прозрачность",
        HintHeic = "iPhone",
        HintWebp = "для сайтов",
        HintAvif = "мало весит",
        HintJxl = "новый",
        HintTiff = "печать",
        HintGif = "анимация",
        HintBmp = "без сжатия",
        HintIco = "иконка",
    };

    // 1 файл, 2 файла, 5 файлов
    private static string FilesRu(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        string word = m10 == 1 && m100 != 11 ? "файл"
            : m10 is >= 2 and <= 4 && (m100 < 10 || m100 >= 20) ? "файла"
            : "файлов";
        return $"{n} {word}";
    }
}

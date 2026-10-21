namespace MdViewer.Core.Documents;

/// <summary>Что делать, когда файл изменила другая программа.</summary>
public enum ExternalChangeAction
{
    /// <summary>Содержимое не изменилось (например, наше собственное сохранение).</summary>
    Ignore,

    /// <summary>Правок нет — обновить вкладку автоматически.</summary>
    Reload,

    /// <summary>Есть несохранённые правки — спросить «Загрузить с диска / Оставить мою версию».</summary>
    Ask,

    /// <summary>Файл удалён или переименован.</summary>
    Deleted,
}

public static class ExternalChangePolicy
{
    /// <param name="fileExists">Существует ли файл сейчас.</param>
    /// <param name="diskText">Текст файла на диске (null — не удалось прочитать).</param>
    /// <param name="lastKnownDiskText">Текст, который был на диске при последней загрузке или сохранении.</param>
    /// <param name="isDirty">Есть ли в вкладке несохранённые правки.</param>
    public static ExternalChangeAction Decide(bool fileExists, string? diskText, string lastKnownDiskText, bool isDirty)
    {
        if (!fileExists)
        {
            return ExternalChangeAction.Deleted;
        }

        if (diskText is null)
        {
            // Файл занят (пишется прямо сейчас) — проверим позже.
            return ExternalChangeAction.Ignore;
        }

        if (string.Equals(TextFileCodec.NormalizeToLf(diskText), TextFileCodec.NormalizeToLf(lastKnownDiskText), StringComparison.Ordinal))
        {
            return ExternalChangeAction.Ignore;
        }

        return isDirty ? ExternalChangeAction.Ask : ExternalChangeAction.Reload;
    }
}

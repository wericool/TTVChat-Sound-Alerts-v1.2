// CommandSetting.cs
using System;
using System.IO; // Добавлено для Path.GetFileName

namespace TwitchChatSoundAlerts
{
    public class CommandSetting
    {
        public string Command { get; set; } // Текст команды, например "!привет"
        public string SoundFilePath { get; set; } // Путь к файлу звука

        // Изменено с минут на секунды
        public int CooldownSeconds { get; set; } // Время кулдауна в секундах
        public int Volume { get; set; } // Громкость от 0 до 100
        
        // Добавляем имя пользователя, запросившего команду
        public string RequestedBy { get; set; } // Имя пользователя, который запросил эту команду

        // Добавляем флаг для режима персонального кулдауна
        public bool IsPersonalCooldown { get; set; } // true - кулдаун только для конкретного пользователя, false - общий кулдаун

        // Вычисляемое свойство для отображения только имени файла в UI
        // JsonSerializer игнорирует его по умолчанию, что нам и нужно.
        public string SoundFileName
        {
            get
            {
                if (string.IsNullOrEmpty(SoundFilePath))
                {
                    return "Файл не выбран";
                }
                try
                {
                    return Path.GetFileName(SoundFilePath);
                }
                catch
                {
                    return "Некорректный путь"; // На случай, если путь некорректный
                }
            }
            // Нет сеттера, так как это свойство только для чтения, основанное на SoundFilePath
        }


        public CommandSetting()
        {
            // Конструктор по умолчанию для десериализации
            // Устанавливаем значения по умолчанию для новых свойств
            CooldownSeconds = 0;
            Volume = 100; // Громкость по умолчанию 100%
            RequestedBy = "anonymous"; // Значение по умолчанию
            IsPersonalCooldown = false; // По умолчанию общий кулдаун
        }

        public CommandSetting(string command, string soundFilePath, int cooldownSeconds = 0, int volume = 100, string requestedBy = "anonymous", bool isPersonalCooldown = false)
        {
            Command = command;
            SoundFilePath = soundFilePath;
            CooldownSeconds = cooldownSeconds;
            Volume = volume;
            RequestedBy = requestedBy;
            IsPersonalCooldown = isPersonalCooldown;
        }

        // Добавляем метод для создания копии с новым именем пользователя
        public CommandSetting CreateCopyWithUsername(string username)
        {
            return new CommandSetting(
                this.Command,
                this.SoundFilePath,
                this.CooldownSeconds,
                this.Volume,
                username,
                this.IsPersonalCooldown
            );
        }
    }
}
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
// Удален using System.Timers; // Не нужен для DispatcherTimer
using System.Windows;
using System.Windows.Controls;
// УДАЛЕНЫ: Microsoft.Web.WebView2.Core, Microsoft.Web.WebView2.Wpf;
using System.Windows.Threading; // Добавлено для DispatcherTimer
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Forms; // Requires <UseWindowsForms>true</UseWindowsForms> in .csproj
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading; // Добавлено для потоков и CancellationToken
using System.Net.Sockets; // Добавлено для TcpClient
using System.Net.Security; // Добавлено для SslStream (для порта 443)
using System.Text; // Добавлено для Encoding
using System.Security.Cryptography.X509Certificates; // Добавлено для SslStream

namespace TwitchChatSoundAlerts
{
public class AppConfiguration
{
    public string LastChannelName { get; set; }
    public ObservableCollection<CommandSetting> CommandSettings { get; set; }

    public AppConfiguration()
    {
        CommandSettings = new ObservableCollection<CommandSetting>();
        LastChannelName = "";
    }
}

    public partial class MainWindow : Window
    {
        public ObservableCollection<CommandSetting> CommandSettings { get; set; }

        private string configFilePath; // Инициализируется в конструкторе
        private string logFilePath;    // Инициализируется в конструкторе
        private StreamWriter logWriter; // Для записи логов в файл
        private object logWriterLock = new object(); // Объект блокировки для многопоточной записи
        private const int MaxLogMessages = 500; // Максимальное количество сообщений в UI логе
        private string NickName = "justinfan"; // Имя пользователя для IRC

        private System.Windows.Media.MediaPlayer mediaPlayer; // Объект MediaPlayer

        private Dictionary<string, DateTime> lastPlayedTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private Queue<CommandSetting> playbackQueue = new Queue<CommandSetting>();
        private bool isPlayingSound = false; // Флаг для отслеживания состояния воспроизведения

        private NotifyIcon notifyIcon;
        private bool isExitingCleanly = false;

        // --- IRC ПОЛЯ ---
        private TcpClient ircClient;
        private Stream ircStream; // NetworkStream или SslStream
        private StreamReader ircReader;
        private StreamWriter ircWriter;
        private CancellationTokenSource ircCancellationTokenSource; // Для отмены чтения из IRC потока
        private Task ircReadTask; // Задача, выполняющая чтение из IRC потока
        private string currentChannel = ""; // Текущий подключенный канал
        // ----------------


        public MainWindow()
        {
            // Инициализация путей к файлам конфигурации и лога в папке Resources
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var resourcesDir = Path.Combine(exeDir, "Resources");
            
            // Убедимся, что директория Resources существует
            if (!Directory.Exists(resourcesDir))
            {
                try
                {
                    Directory.CreateDirectory(resourcesDir);
                }
                catch (Exception ex)
                {
                    // Показываем MessageBox только при старте, если директория не создалась
                    System.Windows.MessageBox.Show($"Не удалось создать директорию для данных приложения: {resourcesDir}\nОшибка: {ex.Message}", "Ошибка инициализации", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            
            configFilePath = Path.Combine(resourcesDir, "config.json");
            logFilePath = Path.Combine(resourcesDir, "app.log");

            InitializeComponent();

            AddLog("===== Приложение запущено =====");
            AddLog($"Путь к файлу конфигурации: {configFilePath}");
            AddLog($"Путь к файлу лога: {logFilePath}");

            CommandSettings = new ObservableCollection<CommandSetting>();
            CommandsDataGrid.ItemsSource = CommandSettings;

            // Инициализация MediaPlayer и подписка на событие ТОЛЬКО ОДИН РАЗ
            mediaPlayer = new System.Windows.Media.MediaPlayer();
            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            AddLog("MediaPlayer инициализирован и MediaEnded подписан.");

            InitializeNotifyIcon();

            this.StateChanged += Window_StateChanged;
            this.Closing += Window_Closing; // Подписываемся на событие Closing
            this.Loaded += Window_Loaded; // Убедимся, что подписаны на Loaded

            SkipButton.IsEnabled = false; // Кнопка пропуска выключена по умолчанию

            // UpdateQueueSizeLabel(); // Обновляем статус очереди при запуске
            AddLog("MainWindow constructor finished.");
        }

        private void InitializeNotifyIcon()
        {
            AddLog("Инициализация NotifyIcon...");
            if (notifyIcon != null)
            {
                AddLog("NotifyIcon уже инициализирован.");
                return;
            }

            notifyIcon = new NotifyIcon();
            
            // Используем иконку самого приложения вместо загрузки из файла
            try
            {
                // Получаем иконку из исполняемого файла приложения
                notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                AddLog("Иконка трея загружена из исполняемого файла приложения");
                }
                catch (Exception ex)
                {
                AddLog($"Ошибка загрузки иконки трея из приложения: {ex.Message}");
                
                // Запасной вариант - стандартная иконка
                try 
                {
                    notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                    AddLog("Установлена стандартная иконка приложения для трея");
                }
                catch 
                {
                    AddLog("Не удалось установить даже стандартную иконку для трея");
                }
            }

            notifyIcon.Text = "TTVChat Sound Alerts v1.2 by ericool";
            notifyIcon.Visible = false;

            // Initialize context menu
            notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon.ContextMenuStrip.Items.Add("Показать", null, (s, e) => NotifyIcon_MouseDoubleClick(s, null));
            notifyIcon.ContextMenuStrip.Items.Add("Выход", null, (s, e) =>
            {
                AddLog("Выбрана команда 'Выход' из трея.");
                isExitingCleanly = true;
                Close(); // Вызов Close() запустит Window_Closing
            });

            notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;
            AddLog("NotifyIcon инициализация завершена.");
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            AddLog($"Событие: Window_StateChanged. NewState={this.WindowState}");
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                if (notifyIcon != null) notifyIcon.Visible = true;
                AddLog("Окно свернуто в трей.");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            AddLog($"Событие: Window_Closing. isExitingCleanly={isExitingCleanly}.");
            
            // Выходим полностью при любом закрытии окна
            isExitingCleanly = true;
            
                // Если завершаем работу cleanly
                AddLog("Приложение полностью завершает работу...");

                SaveConfig();

                // Остановка IRC клиента
                AddLog("Попытка отключения IRC клиента при выходе.");
                DisconnectIrc(); // Вызов метода отключения IRC

                // Остановка и очистка MediaPlayer и очереди
                if (mediaPlayer != null)
                {
                    mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
                    try { mediaPlayer.Stop(); AddLog("MediaPlayer.Stop() вызван при выходе."); } catch { AddLog("Ошибка при MediaPlayer.Stop() при выходе."); }
                    try { mediaPlayer.Close(); AddLog("MediaPlayer.Close() вызван при выходе."); } catch { AddLog("Ошибка при MediaPlayer.Close() при выходе."); }
                    mediaPlayer = null;
                    isPlayingSound = false;
                    AddLog("MediaPlayer освобожден.");
                }

                playbackQueue.Clear();
                AddLog($"Очередь очищена при выходе. Осталось в очереди: {playbackQueue.Count}.");
            UpdateQueueSizeLabel();


                // Удален код сброса WebView2 Source, т.к. WebView2 удален

                // Сброс времен кулдаунов
                lastPlayedTimes.Clear();
                AddLog("Время кулдаунов сброшено.");

                // Удален сброс lastProcessedMessageText

                // Освобождение NotifyIcon
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                    notifyIcon = null;
                    AddLog("Иконка трея освобождена.");
                }

                AddLog("Процесс завершения работы завершен.");
                AddLog("===== Приложение остановлено =====");

                // В этот момент UI поток, возможно, уже завершает работу.
                // Запись последнего лога может не успеть попасть в файл или ListBox.
        }


        private void NotifyIcon_MouseDoubleClick(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            AddLog("Событие: NotifyIcon_MouseDoubleClick.");
            this.Show();
            this.WindowState = WindowState.Normal;
            if (notifyIcon != null) notifyIcon.Visible = false; // Скрываем иконку трея, когда окно видно
            AddLog("Окно показано из трея.");
        }

        // Метод для добавления сообщения в лог (UI безопасным способом)
        private void AddLog(string message)
        {
            try
            {
                // Обычные логи игнорируются
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в AddLog: {ex.Message}");
            }
        }

        // Метод для записи только важных сообщений в лог (теперь обновляет статус)
        private void LogImportant(string message)
        {
            try
            {
                // Обновляем статус с важным сообщением
                string formattedMessage = $"{message}";
                
                // UI обновление следует делать в UI потоке
                if (Dispatcher.Thread == Thread.CurrentThread)
                {
                    // Мы уже в UI потоке, можно обновлять напрямую
                    StatusLabel.Content = formattedMessage;
                }
                else
                {
                    // Мы не в UI потоке, делегируем обновление UI в UI поток
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusLabel.Content = formattedMessage;
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка в LogImportant: {ex.Message}");
            }
        }

        // Метод для обновления статуса команд (информация о командах и кулдаунах)
        private void UpdateCommandStatus(string message)
        {
            // Этот метод больше не показывает статус команд, но сохраняем для совместимости
            LogImportant(message);
        }

        private void AddCommandButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Событие: AddCommandButton_Click - Entry.");
            var dialog = new AddEditCommandDialog();
            dialog.Owner = this; // Устанавливаем владельца для центрирования и модальности

            if (dialog.ShowDialog() == true) // ShowDialog возвращает true, если пользователь нажал OK
            {
                AddLog("Диалог AddEditCommandDialog вернул OK.");
                // Создаем новый объект CommandSetting на основе данных из диалога
                var newSetting = new CommandSetting(dialog.Command, dialog.SoundFilePath, dialog.CooldownSeconds, dialog.Volume, "anonymous", dialog.IsPersonalCooldown);
                AddLog($"Данные из диалога: Command='{newSetting.Command}', File='{newSetting.SoundFilePath}', Cooldown={newSetting.CooldownSeconds}, Volume={newSetting.Volume}, IsPersonalCooldown={newSetting.IsPersonalCooldown}.");


                // Проверяем, существует ли уже команда с таким именем (без учета регистра)
                if (CommandSettings.Any(s => s.Command.Equals(newSetting.Command, StringComparison.OrdinalIgnoreCase)))
                {
                    // Показываем предупреждение, если команда уже есть
                    System.Windows.MessageBox.Show($"Команда '{newSetting.Command}' уже существует.", "Ошибка добавления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    AddLog($"Ошибка: Команда '{newSetting.Command}' уже существует. Добавление отменено.");
                }
                else
                {
                    // Добавляем новую команду в ObservableCollection
                    CommandSettings.Add(newSetting);
                    LogImportant($"Добавлена команда {newSetting.Command}");
                    AddLog($"Добавлена команда '{newSetting.Command}' в CommandSettings.");

                    // Инициализируем время последнего воспроизведения для новой команды
                    lastPlayedTimes[newSetting.Command] = DateTime.MinValue; // DateTime.MinValue означает, что кулдаун сразу готов
                    AddLog($"Initial lastPlayedTimes for '{newSetting.Command}' set to DateTime.MinValue.");
                }
            }
            else
            {
                AddLog("Диалог AddEditCommandDialog отменен.");
            }
            AddLog("Событие: AddCommandButton_Click - Exit.");
        }

        private void EditCommandButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Событие: EditCommandButton_Click - Entry.");
            // Проверяем, выбран ли элемент в DataGrid
            if (CommandsDataGrid.SelectedItem is CommandSetting selectedSetting)
            {
                AddLog($"Выбрана команда для редактирования: '{selectedSetting.Command}'.");
                // Создаем диалог редактирования, передавая текущие данные выбранной команды
                var dialog = new AddEditCommandDialog(selectedSetting.Command, selectedSetting.SoundFilePath, selectedSetting.CooldownSeconds, selectedSetting.Volume, selectedSetting.IsPersonalCooldown);
                dialog.Owner = this;

                if (dialog.ShowDialog() == true) // ShowDialog возвращает true, если пользователь нажал OK
                {
                    AddLog("Диалог AddEditCommandDialog вернул OK (редактирование).");
                    string oldCommand = selectedSetting.Command; // Сохраняем старое имя команды
                    string newCommand = dialog.Command; // Получаем новое имя команды из диалога
                    string oldSoundPath = selectedSetting.SoundFilePath;
                    string newSoundPath = dialog.SoundFilePath;
                    int oldCooldown = selectedSetting.CooldownSeconds;
                    int newCooldown = dialog.CooldownSeconds;
                    int oldVolume = selectedSetting.Volume;
                    int newVolume = dialog.Volume;

                    AddLog($"Старые данные: Cmd='{oldCommand}', File='{oldSoundPath}', CD={oldCooldown}, Vol={oldVolume}.");
                    AddLog($"Новые данные: Cmd='{newCommand}', File='{newSoundPath}', CD={newCooldown}, Vol={newVolume}.");

                    // Дополнить сбор данных о персональном кулдауне:
                    bool oldPersonalCooldown = selectedSetting.IsPersonalCooldown;
                    bool newPersonalCooldown = dialog.IsPersonalCooldown;
                    AddLog($"Персональный кулдаун: старый={oldPersonalCooldown}, новый={newPersonalCooldown}");
                    
                    // И затем найти место, где обновляются значения в объекте selectedSetting:
                        selectedSetting.Command = newCommand;
                        selectedSetting.SoundFilePath = newSoundPath;
                    selectedSetting.CooldownSeconds = newCooldown;
                        selectedSetting.Volume = newVolume;
                    // И дописать обновление флага персонального кулдауна:
                    selectedSetting.IsPersonalCooldown = newPersonalCooldown;

                        // Обновляем отображение в DataGrid (может потребоваться для сложных объектов или если привязка не NotifyPropertyChanged)
                        CommandsDataGrid.Items.Refresh();
                    AddLog($"Команда '{oldCommand}' успешно отредактирована. Новое имя: '{selectedSetting.Command}', IsPersonalCooldown: {selectedSetting.IsPersonalCooldown}");

                        // Обновляем lastPlayedTimes, если имя команды изменилось
                        if (!oldCommand.Equals(selectedSetting.Command, StringComparison.OrdinalIgnoreCase))
                        {
                            if (lastPlayedTimes.ContainsKey(oldCommand))
                            {
                                lastPlayedTimes.Remove(oldCommand);
                                AddLog($"Удалена старая запись кулдауна для '{oldCommand}'.");
                            }
                            // Инициализируем новое имя, если его не было, или обновляем на текущее
                            lastPlayedTimes[selectedSetting.Command] = DateTime.MinValue; // Сброс кулдауна при смене имени
                            AddLog($"Initial lastPlayedTimes for '{selectedSetting.Command}' set to DateTime.MinValue after name change.");
                        }
                        else
                        {
                            // Если имя не менялось, просто убедимся, что запись есть (на всякий случай)
                            if (!lastPlayedTimes.ContainsKey(selectedSetting.Command))
                            {
                                lastPlayedTimes[selectedSetting.Command] = DateTime.MinValue;
                                AddLog($"Initialized missing lastPlayedTimes for '{selectedSetting.Command}'.");
                            }
                        }

                        // Проверка, не играла ли сейчас эта команда и нужно ли остановить плеер
                        // Это выполняется в UI потоке, так что доступ к mediaPlayer безопасен.
                        if (isPlayingSound && mediaPlayer != null && mediaPlayer.Source != null)
                        {
                            try
                            {
                                AddLog($"Обнаружено, что MediaPlayer играет. Проверка соответствия отредактированной команде '{selectedSetting.Command}'.");
                                // Сравниваем путь к файлу, т.к. имя команды могло измениться
                                // Сравниваем локальный путь текущего источника mediaPlayer с новым путем файла команды
                                if (newSoundPath.Equals(mediaPlayer.Source.LocalPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    AddLog($"Играющий звук ('{mediaPlayer.Source.LocalPath}') соответствует отредактированной команде '{selectedSetting.Command}'. Остановка плеера.");
                                    mediaPlayer.Stop(); // Принудительно останавливаем текущий звук
                                                        // MediaEnded обработчик вызовет PlayNextSoundFromQueue
                                }
                                else
                                {
                                    AddLog($"Играющий звук ('{mediaPlayer.Source?.LocalPath ?? "NULL"}') не соответствует отредактированной команде '{selectedSetting.Command}' ('{newSoundPath}'). Воспроизведение не остановлено.");
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLog($"Ошибка при проверке играющего звука после редактирования: {ex.Message}");
                            }
                        }
                        else
                        {
                            AddLog("MediaPlayer не играл или не был инициализирован при редактировании. Проверка играющего звука пропущена.");
                        }
                    }
                }
                else
                {
                    AddLog("Диалог AddEditCommandDialog отменен (редактирование).");
            }
            AddLog("Событие: EditCommandButton_Click - Exit.");
        }


        private void DeleteCommandButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Событие: DeleteCommandButton_Click - Entry.");
            // Проверяем, выбран ли элемент в DataGrid
            if (CommandsDataGrid.SelectedItem is CommandSetting selectedSetting)
            {
                string commandToDelete = selectedSetting.Command; // Сохраняем имя удаляемой команды
                AddLog($"Выбрана команда для удаления: '{commandToDelete}'.");

                // Запрашиваем подтверждение у пользователя
                if (System.Windows.MessageBox.Show($"Вы уверены, что хотите удалить команду '{commandToDelete}'?", "Подтверждение", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
                {
                    AddLog("Пользователь подтвердил удаление.");
                    // Удаляем выбранную команду из ObservableCollection
                    CommandSettings.Remove(selectedSetting);
                    LogImportant($"Удалена команда {commandToDelete}");
                    AddLog($"Команда '{commandToDelete}' удалена из CommandSettings.");

                    // Удаляем запись о времени последнего воспроизведения для удаленной команды
                    if (lastPlayedTimes.ContainsKey(commandToDelete))
                    {
                        lastPlayedTimes.Remove(commandToDelete);
                        AddLog($"Удалена запись кулдауна для '{commandToDelete}'.");
                    }

                    // Создаем новую очередь, исключая из нее удаленную команду
                    var newQueue = new Queue<CommandSetting>();
                    bool removedFromQueue = false;
                    int initialQueueCount = playbackQueue.Count;
                    AddLog($"Начало очистки очереди от команды '{commandToDelete}'. Размер очереди до: {initialQueueCount}.");
                    while (playbackQueue.Count > 0)
                    {
                        var settingInQueue = playbackQueue.Dequeue();
                        // Сравниваем по имени команды (без учета регистра)
                        if (!settingInQueue.Command.Equals(commandToDelete, StringComparison.OrdinalIgnoreCase))
                        {
                            newQueue.Enqueue(settingInQueue); // Добавляем в новую очередь, если это не удаляемая команда
                        }
                        else
                        {
                            removedFromQueue = true; // Флаг, что команда была в очереди
                            AddLog($"Команда '{commandToDelete}' найдена и удалена из очереди.");
                        }
                    }
                    playbackQueue = newQueue; // Заменяем старую очередь новой
                    if (removedFromQueue) {
                        // UpdateQueueSizeLabel(); // Обновляем метку, если что-то удалено из очереди
                    }
                    AddLog($"Очистка очереди завершена. Размер очереди после: {playbackQueue.Count}. Было удалено из очереди: {initialQueueCount - playbackQueue.Count} элементов.");
                    UpdateQueueSizeLabel();


                    // Проверяем, не играет ли сейчас звук, связанный с удаленной командой
                    // Это выполняется в UI потоке, так что доступ к mediaPlayer безопасен.
                    if (isPlayingSound && mediaPlayer != null && mediaPlayer.Source != null)
                    {
                        try
                        {
                            AddLog($"Обнаружено, что MediaPlayer играет. Проверка соответствия удаленной команде '{commandToDelete}'. Текущий Source: {mediaPlayer.Source.LocalPath ?? "NULL"}");

                            // Проходим по всем оставшимся командам, чтобы убедиться, что играющий звук все еще привязан к существующей команде
                            var playingCommand = CommandSettings.FirstOrDefault(s =>
                                 s.SoundFilePath != null &&
                                 mediaPlayer.Source != null &&
                                 Uri.TryCreate(s.SoundFilePath, UriKind.Absolute, out var uri) && // Проверяем, что путь к файлу можно преобразовать в Uri
                                 uri.IsFile && // Убеждаемся, что это файловый URI
                                 uri.LocalPath.Equals(mediaPlayer.Source.LocalPath, StringComparison.OrdinalIgnoreCase) // Сравниваем локальные пути
                            );

                            // Если играющий звук НЕ привязан ни к одной существующей команде,
                            // или если он был привязан именно к удаленной команде (что уже проверено косвенно отсутствием playingCommand)
                            if (playingCommand == null) // playingCommand будет null, если текущий звук был связан с удаленной командой
                            {
                                AddLog($"Играющий звук больше не соответствует существующей команде. Остановка плеера.");
                                mediaPlayer.Stop(); // Принудительно останавливаем текущий звук
                                                    // Вызов Stop() обычно приводит к срабатыванию MediaEnded.
                                                    // Но на всякий случай, явно сбросим флаг и вызовем PlayNextSoundFromQueue
                                isPlayingSound = false; // Сброс флага на случай, если Stop не вызовет MediaEnded
                                SkipButton.IsEnabled = false; // Отключаем кнопку пропуска
                                AddLog("Явно вызван PlayNextSoundFromQueue после остановки играющего звука (команда удалена).");
                                PlayNextSoundFromQueue(); // Пытаемся проиграть следующий, даже если MediaEnded не сработал
                            }
                            else
                            {
                                AddLog($"Играющий звук ('{mediaPlayer.Source?.LocalPath ?? "NULL"}') все еще соответствует команде '{playingCommand.Command}'. Воспроизведение не остановлено.");
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog($"Ошибка при проверке играющего звука после удаления: {ex.Message}");
                        }
                    }
                    else
                    {
                        AddLog("MediaPlayer не играл или не был инициализирован при удалении команды. Проверка играющего звука пропущена.");
                    }
                }
                else
                {
                    AddLog("Пользователь отменил удаление команды.");
                }
            }
            else
            {
                System.Windows.MessageBox.Show("Выберите команду для удаления.", "Предупреждение", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                AddLog("Ошибка: Попытка удаления без выбранной команды.");
            }
            AddLog("Событие: DeleteCommandButton_Click - Exit.");
        }


        private void LoadConfig()
        {
            AddLog("LoadConfig - Entry.");

            // Очищаем текущие данные перед загрузкой
            CommandSettings.Clear();
            lastPlayedTimes.Clear();
            playbackQueue.Clear(); // Очищаем очередь при загрузке новой конфигурации
            // UpdateQueueSizeLabel();
            ChannelNameTextBox.Text = "";
            // Удален сброс lastMessageCount и lastProcessedMessageText
            AddLog("Текущие команды, кулдауны, очередь очищены.");

            // Проверяем существование файла конфигурации
            if (File.Exists(configFilePath))
            {
                AddLog($"Файл конфигурации найден: {configFilePath}. Попытка чтения.");
                try
                {
                    // Читаем весь текст из файла
                    var jsonString = File.ReadAllText(configFilePath);
                    AddLog($"Прочитано {jsonString.Length} символов из файла конфигурации.");
                    var options = new JsonSerializerOptions { WriteIndented = true }; // Опции для форматирования JSON
                    // Десериализуем JSON строку в объект AppConfiguration
                    var config = JsonSerializer.Deserialize<AppConfiguration>(jsonString, options);
                    AddLog($"Десериализация JSON завершена. config is NULL: {config == null}.");

                    // Проверяем, успешно ли десериализован объект и содержит ли он данные
                    if (config != null)
                    {
                        // Загружаем настройки команд, если они есть
                        if (config.CommandSettings != null)
                        {
                            AddLog($"Загрузка команд из конфигурации. Найдено {config.CommandSettings.Count} записей.");
                            foreach (var setting in config.CommandSettings)
                            {
                                CommandSettings.Add(setting); // Добавляем каждую команду в ObservableCollection
                                // Инициализируем времена последнего воспроизведения для загруженных команд
                                lastPlayedTimes[setting.Command] = DateTime.MinValue;
                                AddLog($" - Загружена команда '{setting.Command}'. Initial CD set to MinValue.");
                            }
                            AddLog($"Загружено {CommandSettings.Count} команд в ObservableCollection.");
                        }
                        else
                        {
                            AddLog("В файле конфигурации не найдено команд ('CommandSettings' равно null). Загружена пустая коллекция.");
                        }

                        // Загружаем последнее имя канала
                        ChannelNameTextBox.Text = config.LastChannelName ?? ""; // ?? "" - если LastChannelName null, используем пустую строку
                        AddLog($"Загружено последнее имя канала: '{ChannelNameTextBox.Text}'.");
                    }
                    else
                    {
                        // Если файл пуст или некорректен, сообщаем об этом
                        AddLog("Файл конфигурации пуст или некорректен (результат десериализации null). Загружена пустая конфигурация.");
                    }
                }
                catch (JsonException jsonEx)
                {
                    // Обработка ошибок парсинга JSON
                    AddLog($"Ошибка парсинга JSON файла конфигурации: {jsonEx.Message}");
                    System.Windows.MessageBox.Show($"Ошибка парсинга JSON файла конфигурации: {jsonEx.Message}\nФайл может быть поврежден.", "Ошибка загрузки", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    // Общая обработка других ошибок при загрузке
                    AddLog($"Общая ошибка загрузки конфигурации: {ex.Message}");
                    System.Windows.MessageBox.Show($"Ошибка загрузки конфигурации: {ex.Message}", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            else
            {
                // Если файл не найден, сообщаем об этом
                AddLog($"Файл конфигурации не найден по пути: {configFilePath}. Загружена пустая конфигурация.");
            }
            AddLog("LoadConfig - Exit.");
        }

        private void SaveConfig()
        {
            AddLog("SaveConfig - Entry.");
            try
            {
                // Получаем директорию для файла конфигурации
                var dir = Path.GetDirectoryName(configFilePath);
                AddLog($"Проверка директории конфигурации: {dir}");
                // Создаем директорию, если она не существует
                if (!Directory.Exists(dir))
                {
                    AddLog("Директория конфигурации не найдена. Попытка создания.");
                    Directory.CreateDirectory(dir);
                    AddLog("Директория конфигурации создана.");
                }

                // Создаем объект конфигурации для сохранения
                var config = new AppConfiguration
                {
                    LastChannelName = ChannelNameTextBox.Text,
                    CommandSettings = CommandSettings // CommandSettings - это ObservableCollection из UI
                };
                AddLog($"Конфигурация для сохранения: Канал='{config.LastChannelName}', Команд={config.CommandSettings.Count}.");

                // Сериализуем объект конфигурации в JSON строку с отступами
                var options = new JsonSerializerOptions { WriteIndented = true };
                var jsonString = JsonSerializer.Serialize(config, options);
                // Записываем JSON строку в файл
                File.WriteAllText(configFilePath, jsonString);
                AddLog($"Конфигурация успешно сохранена в {configFilePath}. Размер данных: {jsonString.Length} символов.");
            }
            catch (Exception ex)
            {
                // Обработка ошибок сохранения
                AddLog($"Ошибка сохранения конфигурации в {configFilePath}: {ex.Message}");
                System.Windows.MessageBox.Show($"Ошибка сохранения конфигурации: {ex.Message}", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            AddLog("SaveConfig - Exit.");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("Событие: Window_Loaded - Entry.");
            // Загружаем конфигурацию при загрузке окна
            LoadConfig();
            AddLog("TTVChat Sound Alerts v1.2 by ericool запущен и готов к работе.");

            // Инициализируем метку размера очереди
            UpdateQueueSizeLabel();

            AddLog("Событие: Window_Loaded - Exit.");
        }


        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string input = ChannelNameTextBox.Text.Trim();
            string channelName = input.ToLower();

            if (string.IsNullOrEmpty(input))
            {
                System.Windows.MessageBox.Show("Введите имя канала Twitch или ссылку на канал.", "Предупреждение", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            // Регулярное выражение для извлечения имени канала из URL Twitch
            var twitchUrlPattern = @"^(?:https?:\/\/)?(?:www\.)?twitch\.tv\/([a-zA-Z0-9_]+)(?:\/.*)?$";
            var match = Regex.Match(input, twitchUrlPattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                channelName = match.Groups[1].Value.ToLower();
            }
            else
            {
                if (!Regex.IsMatch(input, @"^[a-zA-Z0-9_]{4,25}$"))
                {
                    System.Windows.MessageBox.Show("Некорректное имя канала или URL. Имя канала должно содержать только буквы, цифры и подчеркивания (от 4 до 25 символов).", "Предупреждение", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                channelName = input.ToLower();
            }

            // Отключаемся, если уже подключены, чтобы избежать множественных подключений
            if (ircClient != null && ircClient.Connected)
            {
                DisconnectIrc();
            }

            AddLog($"Попытка подключения к IRC чату канала '{channelName}'...");
            Dispatcher.Invoke(() => {
                StatusLabel.Content = "Попытка подключения...";
                UpdateQueueSizeLabel();
            });

            try
            {
                // Инициализация IRC клиента
                ircClient = new TcpClient();
                ircClient.ReceiveTimeout = 5000; // 5 секунд на получение
                ircClient.SendTimeout = 5000; // 5 секунд на отправку

                // Подключение к серверу
                await ircClient.ConnectAsync("irc.chat.twitch.tv", 443);

                // Настройка SSL Stream
                ircStream = ircClient.GetStream();
                SslStream sslStream = new SslStream(ircStream, false, new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
                await sslStream.AuthenticateAsClientAsync("irc.chat.twitch.tv");
                ircStream = sslStream;

                ircReader = new StreamReader(ircStream, Encoding.UTF8);
                ircWriter = new StreamWriter(ircStream, Encoding.UTF8) { AutoFlush = true };

                // Отправка IRC команд
                await ircWriter.WriteLineAsync("PASS oauth:"); // Анонимный пароль
                await ircWriter.WriteLineAsync("NICK justinfan" + new Random().Next(10000, 99999)); // Анонимный ник
                await ircWriter.WriteLineAsync($"JOIN #{channelName}");

                currentChannel = channelName; // Сохраняем имя текущего канала

                // Создаем CancellationTokenSource для возможности отмены задачи чтения
                ircCancellationTokenSource = new CancellationTokenSource();
                // Запускаем задачу для чтения сообщений из IRC потока в фоновом потоке
                ircReadTask = Task.Run(() => ReadIrcMessagesAsync(ircCancellationTokenSource.Token));

                // Изменяем состояние кнопок и статус в UI потоке
                Dispatcher.Invoke(() =>
                {
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = true;
                    SkipButton.IsEnabled = false;
                    StatusLabel.Content = "Подключен"; // Упрощенный основной статус
                });

                // Очистка очереди и сброс кулдаунов при подключении к новому каналу
                playbackQueue.Clear();
                lastPlayedTimes.Clear();
                foreach (var setting in CommandSettings)
                {
                    lastPlayedTimes[setting.Command] = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка подключения к IRC чату: {ex.Message}");
                // Убедимся, что все ресурсы IRC освобождены в случае ошибки подключения
                CleanupIrcResources();

                // Обновляем состояние UI в UI потоке
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Content = $"Ошибка подключения: {ex.Message}";
                    UpdateQueueSizeLabel();
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    SkipButton.IsEnabled = false;
                });
            }
        }

        // Callback для валидации SSL сертификата (простая реализация, всегда возвращает true)
        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // WARNING: Ignoring SSL certificate validation is insecure!
            // This is simplified for demonstration. In a production app,
            // you should validate the certificate chain and trust.
            AddLog($"SSL Certificate Validation Callback: Policy Errors: {sslPolicyErrors}. Ignoring validation.");
            return true; // Accept all certificates for simplicity (INSECURE!)
        }


        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            DisconnectIrc(); // Вызываем метод отключения IRC
        }

        // Метод для отключения от IRC
        private void DisconnectIrc()
        {
            // Отменяем задачу чтения
            if (ircCancellationTokenSource != null)
            {
                ircCancellationTokenSource.Cancel(); // Сигнализируем задаче чтения об отмене
                ircCancellationTokenSource.Dispose();
                ircCancellationTokenSource = null;
            }

            // Очищаем ресурсы IRC
            CleanupIrcResources();

            // Обновляем состояние UI в UI потоке
            Dispatcher.Invoke(() =>
            {
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                SkipButton.IsEnabled = false; // Кнопка пропуска выключена при отключении/ошибке
                StatusLabel.Content = "Отключен";
            });
        }

        // Метод для очистки ресурсов IRC клиента
        private void CleanupIrcResources()
        {
            // Закрываем все ресурсы IRC
            if (ircWriter != null)
            {
                try { ircWriter.Dispose(); } catch { }
                ircWriter = null;
            }
            if (ircReader != null)
            {
                try { ircReader.Dispose(); } catch { }
                ircReader = null;
            }
            if (ircStream != null)
            {
                try { ircStream.Dispose(); } catch { }
                ircStream = null;
            }
            if (ircClient != null)
            {
                try { ircClient.Dispose(); } catch { }
                ircClient = null;
            }
            currentChannel = ""; // Сбрасываем имя канала
        }


        // Задача для чтения сообщений из IRC потока
        private async Task ReadIrcMessagesAsync(CancellationToken cancellationToken)
        {
            // Проверяем наличие reader перед входом в цикл
            if (ircReader == null)
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    Dispatcher.Invoke(() => DisconnectIrc());
                }
                return;
            }

            // Цикл чтения сообщений
            while (!cancellationToken.IsCancellationRequested)
            {
                string line = null;
                try
                {
                    // Читаем строку из IRC потока асинхронно
                    line = await ircReader.ReadLineAsync().ConfigureAwait(false);

                    // Проверка на отмену сразу после await
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break; // Выходим из цикла
                    }

                    if (line == null)
                    {
                        // ReadLineAsync возвращает null, если соединение закрыто на другом конце
                        LogImportant("Соединение закрыто сервером");
                        break; // Выходим из цикла
                    }

                    // Обработка полученной строки в UI потоке
                    if (!Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.InvokeAsync(() => ProcessIrcMessageLine(line), DispatcherPriority.Normal);
                    }
                    else
                    {
                        break; // Выходим из цикла, если Dispatcher недоступен
                    }
                }
                catch (Exception ex)
                {
                    // Обработка исключений в задаче чтения
                    if (!(ex is OperationCanceledException || ex is ObjectDisposedException))
                    {
                        AddLog($"Ошибка при чтении IRC: {ex.Message}");
                    }
                    break; // Выходим из цикла
                }
            }

            // Если выход из цикла произошел не из-за запроса отмены, но ресурсы активны
            if (!cancellationToken.IsCancellationRequested &&
                (ircClient?.Connected ?? false || ircStream != null || ircReader != null || ircWriter != null) &&
                !Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(() => DisconnectIrc());
            }
        }


        // Метод для обработки одной строки IRC сообщения (выполняется в UI потоке)
        private void ProcessIrcMessageLine(string line)
        {
            // Если сообщение PING, отвечаем PONG
            if (line.StartsWith("PING"))
            {
                SendIrcMessage("PONG " + line.Substring(5));
                return;
            }

            // Обработка только PRIVMSG (чат)
            if (line.Contains("PRIVMSG"))
            {
                // Примеры формата сообщений:
                // :username!username@username.tmi.twitch.tv PRIVMSG #channel :message text here

                // Проверяем что сообщение для текущего канала
                if (!line.Contains($"PRIVMSG #{currentChannel} :"))
                return;

                try
                {
                    // Извлекаем имя пользователя
                    int usernameStart = line.IndexOf(':') + 1;
                    int usernameEnd = line.IndexOf('!');
                    if (usernameStart < 0 || usernameEnd < 0 || usernameStart >= usernameEnd)
                return;

                    string username = line.Substring(usernameStart, usernameEnd - usernameStart);

            // Извлекаем текст сообщения
                    int messageStart = line.IndexOf($"PRIVMSG #{currentChannel} :") + $"PRIVMSG #{currentChannel} :".Length;
                    if (messageStart < 0 || messageStart >= line.Length)
                        return;

                    string message = line.Substring(messageStart);

                    // Обрабатываем сообщение от пользователя
                    ProcessChatMessage(username, message);
                }
                catch (Exception ex)
                {
                    LogImportant($"Ошибка при обработке IRC сообщения: {ex.Message}");
                }
            }
        }


        // Метод для обработки одного сообщения чата (вызывается из ProcessIrcMessageLine в UI потоке)
        private void ProcessChatMessage(string username, string message)
        {
            try
            {
                // Проверяем, не наше ли это собственное сообщение, чтобы избежать рекурсии
                if (username.Equals(NickName, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"ProcessChatMessage: Игнорируем собственное сообщение: '{message}'");
                    return;
                }

                // Проверяем, содержит ли сообщение команду
                foreach (var command in CommandSettings)
                {
                    if (message.StartsWith(command.Command, StringComparison.OrdinalIgnoreCase))
                    {
                        // Получена команда, но не показываем в статусе
                        AddLog($"Получена команда {command.Command} от пользователя {username}");
                        
                        // Подходим ли мы критериям проверки кулдауна
                        if (IsCommandOnCooldown(command, username, out TimeSpan remainingTime))
                        {
                            // Команда на кулдауне, но не показываем в статусе
                            if (command.IsPersonalCooldown)
                            {
                                AddLog($"Команда {command.Command} в кулдауне у пользователя {username}. Осталось: {remainingTime.TotalSeconds:F1} сек");
                            }
                            else
                            {
                                AddLog($"Команда {command.Command} в общем кулдауне. Осталось: {remainingTime.TotalSeconds:F1} сек");
                            }
                            continue;
                        }

                        // Выполнить команду
                        ExecuteCommand(command, username);
                        break; // Остановиться после первого совпадения команды
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка при обработке сообщения от {username}: {ex.Message}");
            }
        }


        // Метод для воспроизведения следующего звука из очереди
        private void PlayNextSoundFromQueue()
        {
            // Убедимся, что мы работаем в UI потоке, т.к. MediaPlayer и UI контролы чувствительны к потокам
            // Этот метод теперь всегда вызывается из UI потока (из ProcessChatMessage или MediaPlayer_MediaEnded),
            // но проверка оставлена для надежности.
            if (Dispatcher.CheckAccess() == false)
            {
                AddLog("PlayNextSoundFromQueue вызван не в UI потоке! Диспетчеризация через Invoke...");
                Dispatcher.Invoke(() => PlayNextSoundFromQueue());
                return;
            }

            // Мы в UI потоке.
            AddLog($"PlayNextSoundFromQueue - Entry. Current Thread ID: {Thread.CurrentThread.ManagedThreadId}. isPlayingSound={isPlayingSound}, Очередь={playbackQueue.Count}.");


            // Если очередь пуста ИЛИ мы уже находимся в процессе воспроизведения (isPlayingSound == true),
            // то просто выходим. isPlayingSound будет сброшен в MediaPlayer_MediaEnded или в catch блоке.
            if (playbackQueue.Count == 0 || isPlayingSound)
            {
                // Если очередь пуста и мы НЕ играем, убедимся, что статус UI правильный
                if (playbackQueue.Count == 0 && !isPlayingSound)
                {
                    AddLog("PlayNextSoundFromQueue: Очередь пуста и isPlayingSound=false. Обновление статуса UI.");
                    // Обновляем статус UI, когда очередь пуста и нет текущего воспроизведения.
                    // Проверяем состояние IRC клиента, чтобы понять, подключены мы или нет.
                    if (ircClient != null && ircClient.Connected)
                    {
                        StatusLabel.Content = "Ожидание команды";
                    }
                    else
                    {
                        StatusLabel.Content = "Отключен";
                    }
                }
                else if (playbackQueue.Count > 0 && isPlayingSound)
                {
                    // Если очередь не пуста, но уже играет, просто выходим.
                    AddLog($"PlayNextSoundFromQueue: isPlayingSound=true и очередь не пуста ({playbackQueue.Count}). Выход.");
                }
                else if (playbackQueue.Count == 0 && isPlayingSound)
                {
                    // Это странное состояние - очередь пуста, но isPlayingSound true.
                    // Возможно, звук только что закончился, и MediaEnded еще не сбросил флаг?
                    // Или произошла ошибка воспроизведения.
                    AddLog($"PlayNextSoundFromQueue: Странное состояние - Очередь пуста, но isPlayingSound=true. Сброс isPlayingSound и выход.");
                    isPlayingSound = false; // Сбросим флаг на всякий случай
                    SkipButton.IsEnabled = false;
                }
                else if (playbackQueue.Count > 0 && !isPlayingSound)
                {
                    // Эта ветка не должна достигаться при правильной логике входа, т.к.
                    // если очередь > 0 И !isPlayingSound, мы не должны были зайти в этот if.
                    // Добавлено на всякий случай для логгирования.
                    AddLog($"PlayNextSoundFromQueue: Неожиданное состояние - Очередь > 0, но isPlayingSound=false. Это должно было быть обработано ниже.");
                }


                AddLog("PlayNextSoundFromQueue - Exit (очередь пуста или isPlayingSound true).");
                return; // Выходим, если нет ничего в очереди или уже играем
            }

            // Если мы дошли сюда, значит, очередь НЕ пуста И isPlayingSound == false.
            // Это безопасное состояние для начала воспроизведения следующего звука.

            CommandSetting nextSetting = null;
            try
            {
                nextSetting = playbackQueue.Dequeue(); // Извлекаем следующий элемент из очереди
                AddLog($"PlayNextSoundFromQueue: Извлечена команда '{nextSetting.Command}' из очереди. В очереди осталось: {playbackQueue.Count}.");
            }
            catch (InvalidOperationException)
            {
                // Очередь оказалась пустой между проверкой count и Dequeue.
                // Это редкий race condition, но его стоит обработать.
                AddLog("PlayNextSoundFromQueue: Ошибка при Dequeue - очередь оказалась пустой. Сброс флага и выход.");
                isPlayingSound = false; // Убедимся, что флаг сброшен
                SkipButton.IsEnabled = false;
                return; // Просто выходим
            }


            // *Надежный сброс MediaPlayer перед воспроизведением нового звука*
            // Это важный шаг для предотвращения зависаний.
            if (mediaPlayer != null)
            {
                AddLog("PlayNextSoundFromQueue: Попытка Stop() и Close() перед новым воспроизведением.");
                try { mediaPlayer.Stop(); AddLog("PlayNextSoundFromQueue: MediaPlayer.Stop() вызван."); } catch (Exception ex) { AddLog($"PlayNextSoundFromQueue: Ошибка при MediaPlayer.Stop() перед новым воспроизведением: {ex.Message}"); }
                try { mediaPlayer.Close(); AddLog("PlayNextSoundFromQueue: MediaPlayer.Close() вызван."); } catch (Exception ex) { AddLog($"PlayNextSoundFromQueue: Ошибка при MediaPlayer.Close() перед новым воспроизведением: {ex.Message}"); }
                AddLog("PlayNextSoundFromQueue: Stop() и Close() попытки завершены.");
            }
            else
            {
                // Если mediaPlayer вдруг стал null (чего быть не должно после конструктора), пересоздаем его.
                AddLog("PlayNextSoundFromQueue: MediaPlayer оказался NULL перед воспроизведением. Пересоздание...");
                mediaPlayer = new System.Windows.Media.MediaPlayer();
                mediaPlayer.MediaEnded += MediaPlayer_MediaEnded; // Подписываемся снова
                AddLog("PlayNextSoundFromQueue: MediaPlayer пересоздан и событие MediaEnded подписано.");
            }


            // Проверяем существование файла звука
            if (!File.Exists(nextSetting.SoundFilePath))
            {
                AddLog($"PlayNextSoundFromQueue: Ошибка! Файл звука не найден для команды '{nextSetting.Command}': '{Path.GetFileName(nextSetting.SoundFilePath)}'. Пропускаю.");
                StatusLabel.Content = $"Файл не найден: {Path.GetFileName(nextSetting.SoundFilePath)}";
                AddLog("PlayNextSoundFromQueue: Статус UI обновлен: Файл не найден.");

                isPlayingSound = false; // Важно: сбрасываем флаг при ошибке файла
                SkipButton.IsEnabled = false; // Убедимся, что кнопка отключена
                AddLog("PlayNextSoundFromQueue: isPlayingSound = false, SkipButton = false.");

                // Не нужно останавливать/закрывать плеер здесь, т.к. мы уже сделали это выше.
                // try { mediaPlayer?.Stop(); } catch { }
                // try { mediaPlayer?.Close(); } catch { }

                AddLog("PlayNextSoundFromQueue: Вызов PlayNextSoundFromQueue для следующего звука.");
                PlayNextSoundFromQueue(); // *Рекурсивный вызов* для обработки следующего звука в очереди
                AddLog("PlayNextSoundFromQueue - Exit (Файл не найден).");
                return; // Прерываем выполнение этого вызова
            }

            // Если файл существует, пытаемся проиграть звук
            try
            {
                AddLog($"PlayNextSoundFromQueue: Попытка открытия и воспроизведения файла: '{Path.GetFileName(nextSetting.SoundFilePath)}' для команды '{nextSetting.Command}'.");

                isPlayingSound = true; // *Устанавливаем флаг ТОЛЬКО непосредственно перед попыткой проиграть*
                AddLog("PlayNextSoundFromQueue: isPlayingSound = true.");

                // Ограничиваем громкость диапазоном от 0 до 100
                int clampedVolume = Math.Max(0, Math.Min(100, nextSetting.Volume));
                double mediaVolume = (double)clampedVolume / 100.0; // Преобразуем в диапазон 0.0 - 1.0
                AddLog($"PlayNextSoundFromQueue: Громкость для команды '{nextSetting.Command}' установлена: {clampedVolume}% ({mediaVolume:F2}).");


                // Устанавливаем громкость перед открытием (рекомендуется)
                if (mediaPlayer != null)
                {
                    mediaPlayer.Volume = mediaVolume;
                }
                else
                {
                    AddLog("PlayNextSoundFromQueue: MediaPlayer NULL при попытке установить громкость.");
                    throw new InvalidOperationException("MediaPlayer is null for volume."); // Имитируем ошибку
                }


                // Открываем файл. Это может вызвать исключение, если файл некорректен.
                if (mediaPlayer != null)
                {
                    AddLog($"PlayNextSoundFromQueue: Вызов mediaPlayer.Open(new Uri(\"{nextSetting.SoundFilePath}\")).");
                    mediaPlayer.Open(new Uri(nextSetting.SoundFilePath));
                    AddLog("PlayNextSoundFromQueue: mediaPlayer.Open() завершен.");
                }
                else
                {
                    AddLog("PlayNextSoundFromQueue: MediaPlayer NULL при попытке открыть файл.");
                    throw new InvalidOperationException("MediaPlayer is null for open."); // Имитируем ошибку для перехода в catch
                }


                // Воспроизводим звук. Это может вызвать исключение.
                if (mediaPlayer != null)
                {
                    AddLog("PlayNextSoundFromQueue: Вызов mediaPlayer.Play().");
                    mediaPlayer.Play();
                    AddLog("PlayNextSoundFromQueue: mediaPlayer.Play() завершен.");
                }
                else
                {
                    AddLog("PlayNextSoundFromQueue: MediaPlayer NULL при попытке проиграть.");
                    throw new InvalidOperationException("MediaPlayer is null for play."); // Имитируем ошибку для перехода в catch
                }


                // Обновляем статус UI
                StatusLabel.Content = $"Воспроизвожу команду {nextSetting.Command}";
                SkipButton.IsEnabled = true; // Включаем кнопку пропуска, т.к. звук играет
            }
            catch (Exception ex)
            {
                // Обработка любых ошибок при попытке воспроизведения
                AddLog($"PlayNextSoundFromQueue: КРИТИЧЕСКАЯ ОШИБКА при попытке проиграть звук для команды '{nextSetting.Command}': {ex.Message}");
                StatusLabel.Content = $"Ошибка звука для {nextSetting.Command}";
                AddLog("PlayNextSoundFromQueue: Статус UI обновлен: Ошибка звука.");

                isPlayingSound = false; // *Важно: сбрасываем флаг при ошибке воспроизведения*
                AddLog("PlayNextSoundFromQueue: isPlayingSound = false (из блока catch).");

                // Попытка остановить и закрыть плеер даже при ошибке
                AddLog("PlayNextSoundFromQueue: Попытка Stop() и Close() в блоке catch.");
                try { mediaPlayer?.Stop(); AddLog("PlayNextSoundFromQueue: MediaPlayer.Stop() вызван в catch."); } catch { AddLog("PlayNextSoundFromQueue: Ошибка при MediaPlayer.Stop() в catch."); }
                try { mediaPlayer?.Close(); AddLog("PlayNextSoundSoundFromQueue: Ошибка при MediaPlayer.Close() в catch."); } catch { AddLog("PlayNextSoundSoundFromQueue: Ошибка при MediaPlayer.Close() в catch."); }
                AddLog("PlayNextSoundFromQueue: Stop() и Close() попытки в catch завершены.");


                SkipButton.IsEnabled = false; // Отключаем кнопку пропуска
                AddLog("PlayNextSoundFromQueue: SkipButton = false.");

                AddLog("PlayNextSoundFromQueue: Вызов PlayNextSoundFromQueue для обработки следующего звука.");
                PlayNextSoundFromQueue(); // *Рекурсивный вызов* для обработки следующего звука в очереди
                AddLog("PlayNextSoundFromQueue - Exit (Ошибка воспроизведения).");
            }
        }

        // Обработчик события завершения воспроизведения MediaPlayer (выполняется в UI потоке)
        private void MediaPlayer_MediaEnded(object sender, EventArgs e)
        {
            // Убедимся, что плеер существует и закроем его
            if (mediaPlayer != null)
            {
                try { mediaPlayer.Close(); } catch { }
            }

            isPlayingSound = false; // Сбрасываем флаг после завершения воспроизведения
            SkipButton.IsEnabled = false; // Отключаем кнопку пропуска

            PlayNextSoundFromQueue(); // Пытаемся проиграть следующий звук из очереди
        }


        // Обработчик кнопки "Пропустить" (выполняется в UI потоке)
        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isPlayingSound)
            {
                return;
            }

            if (mediaPlayer != null)
            {
                try
                {
                    mediaPlayer.Stop();
                    mediaPlayer.Close();
                    LogImportant("Воспроизведение пропущено");
                }
                catch (Exception ex)
                {
                    AddLog($"Ошибка при попытке остановить воспроизведение: {ex.Message}");
                }
                
                // Сбрасываем флаг и обрабатываем завершение
                isPlayingSound = false;
                SkipButton.IsEnabled = false;
                
                // Воспроизводим следующий звук в очереди
                PlayNextSoundFromQueue();
            }
        }


        // Метод для обновления метки размера очереди в UI (выполняется в UI потоке)
        private void UpdateQueueSizeLabel()
        {
            // Этот метод больше не показывает информацию о размере очереди
        }

        // Обработчик события Tick для таймера прогресс бара
        private void ProgressBarTimer_Tick(object sender, EventArgs e)
        {
            /* Метод отключен за ненадобностью
            // Вычисляем прогресс на основе прошедшего времени с момента начала
            double elapsedMs = (DateTime.Now - currentProgressBarStartTime).TotalMilliseconds;
            double progressValue = elapsedMs % PROGRESS_BAR_INTERVAL_MS;
            
            // Если значение выходит за пределы, сбрасываем его
            if (progressValue > PROGRESS_BAR_INTERVAL_MS || progressValue < 0)
            {
                progressValue = 0;
                currentProgressBarStartTime = DateTime.Now;
            }
            
            // Обновляем значение прогресс бара
            ScrapingProgressBar.Value = progressValue;
            */
        }

        // Метод проверки, находится ли команда на кулдауне
        private bool IsCommandOnCooldown(CommandSetting command, string username, out TimeSpan remainingTime)
        {
            // Получаем время последнего воспроизведения команды
            DateTime lastPlayed;
            if (!lastPlayedTimes.TryGetValue(command.Command, out lastPlayed))
            {
                lastPlayed = DateTime.MinValue;
                lastPlayedTimes[command.Command] = lastPlayed;
            }

            // Вычисляем, сколько времени прошло с последнего воспроизведения
            TimeSpan elapsed = DateTime.Now - lastPlayed;
            
            // Проверяем, нужно ли персональный кулдаун
            bool isPersonal = command.IsPersonalCooldown;
            
            // Время кулдауна команды в секундах
            int cooldownSeconds = command.CooldownSeconds;
            
            // Если времени прошло больше, чем cooldownSeconds, то команда не на кулдауне
            if (elapsed.TotalSeconds >= cooldownSeconds)
            {
                remainingTime = TimeSpan.Zero;
                return false; // Команда не на кулдауне
            }
            
            // Команда на кулдауне, вычисляем оставшееся время
            remainingTime = TimeSpan.FromSeconds(cooldownSeconds) - elapsed;
            return true; // Команда на кулдауне
        }

        // Метод выполнения команды
        private void ExecuteCommand(CommandSetting command, string username)
        {
            try
            {
                // Обновляем время последнего воспроизведения
                lastPlayedTimes[command.Command] = DateTime.Now;
                
                // Создаем копию настройки с информацией о запросившем
                var queueItem = new CommandSetting(
                    command.Command,
                    command.SoundFilePath,
                    command.CooldownSeconds,
                    command.Volume,
                    username,
                    command.IsPersonalCooldown
                );
                
                // Добавляем в очередь
                playbackQueue.Enqueue(queueItem);
                AddLog($"Команда '{command.Command}' от '{username}' добавлена в очередь. Текущий размер очереди: {playbackQueue.Count}");
                
                // Если нет текущего воспроизведения, запускаем
                if (!isPlayingSound)
                {
                    AddLog($"Немедленный запуск воспроизведения для команды '{command.Command}' от '{username}'.");
                    PlayNextSoundFromQueue();
                }
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка выполнения команды '{command.Command}': {ex.Message}");
            }
        }

        // Отправка сообщения в IRC чат
        private void SendIrcMessage(string message)
        {
            try
            {
                if (ircWriter != null)
                {
                    ircWriter.WriteLine(message);
                    AddLog($"IRC > {message}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка отправки IRC сообщения: {ex.Message}");
            }
        }
    }
}
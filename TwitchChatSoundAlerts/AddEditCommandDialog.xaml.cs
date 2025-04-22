// AddEditCommandDialog.xaml.cs
using System;
using System.Windows;
using Microsoft.Win32; // Для OpenFileDialog (WPF)
using System.Globalization;

namespace TwitchChatSoundAlerts
{
    public partial class AddEditCommandDialog : Window
    {
        public string Command { get; private set; }
        public string SoundFilePath { get; private set; }
        public int CooldownSeconds { get; private set; }
        public int Volume { get; private set; }
        public bool IsPersonalCooldown { get; private set; }

        public AddEditCommandDialog()
        {
            InitializeComponent();
            CooldownTextBox.Text = "0";
            VolumeTextBox.Text = "100";
            PersonalCooldownCheckBox.IsChecked = false;
        }

        public AddEditCommandDialog(string currentCommand, string currentSoundFilePath, int currentCooldown, int currentVolume, bool isPersonalCooldown = false)
        {
            InitializeComponent();
            CommandTextBox.Text = currentCommand;
            SoundFilePathTextBox.Text = currentSoundFilePath;
            CooldownTextBox.Text = currentCooldown.ToString();
            VolumeTextBox.Text = currentVolume.ToString();
            PersonalCooldownCheckBox.IsChecked = isPersonalCooldown;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // Явно используем OpenFileDialog из Microsoft.Win32 (WPF)
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "MP3 файлы (*.mp3)|*.mp3|Все файлы (*.*)|*.*";
            openFileDialog.Title = "Выберите звуковой файл команды (MP3)";

            if (openFileDialog.ShowDialog() == true)
            {
                SoundFilePathTextBox.Text = openFileDialog.FileName;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommandTextBox.Text))
            {
                // Явно используем MessageBox из System.Windows (WPF)
                System.Windows.MessageBox.Show("Пожалуйста, введите команду.", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(SoundFilePathTextBox.Text))
            {
                // Явно используем MessageBox из System.Windows (WPF)
                System.Windows.MessageBox.Show("Пожалуйста, выберите файл звука.", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(CooldownTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cooldown) || cooldown < 0)
            {
                // Явно используем MessageBox из System.Windows (WPF)
                System.Windows.MessageBox.Show("Пожалуйста, введите корректное неотрицательное число для кулдауна (в секундах).", "Ошибка ввода", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(VolumeTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int volume) || volume < 0 || volume > 100)
            {
                // Явно используем MessageBox из System.Windows (WPF)
                System.Windows.MessageBox.Show("Пожалуйста, введите корректное число для громкости (от 0 до 100).", "Ошибка ввода", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (!CommandTextBox.Text.Trim().StartsWith("!"))
            {
                CommandTextBox.Text = "!" + CommandTextBox.Text.Trim();
            }

            if (!System.IO.File.Exists(SoundFilePathTextBox.Text.Trim()))
            {
                // Явно используем MessageBox из System.Windows (WPF)
                System.Windows.MessageBox.Show("Выбранный файл звука не найден.", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            this.Command = CommandTextBox.Text.Trim();
            this.SoundFilePath = SoundFilePathTextBox.Text.Trim();
            this.CooldownSeconds = cooldown;
            this.Volume = volume;
            this.IsPersonalCooldown = PersonalCooldownCheckBox.IsChecked ?? false;

            DialogResult = true;
        }
    }
}
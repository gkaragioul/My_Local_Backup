using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace MyLocalBackup.UI.Views
{
    public partial class BackupNotificationWindow : Window
    {
        private readonly DispatcherTimer _closeTimer;

        public BackupNotificationWindow()
        {
            InitializeComponent();

            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _closeTimer.Tick += CloseTimer_Tick;

            Loaded += BackupNotificationWindow_Loaded;
            Closed += (s, e) => _closeTimer.Stop();
        }

        private void BackupNotificationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            _closeTimer.Start();
        }

        private void PositionWindow()
        {
            var desktopWorkingArea = SystemParameters.WorkArea;
            this.Left = desktopWorkingArea.Right - this.Width - 10;
            this.Top = desktopWorkingArea.Bottom - this.Height - 10;
        }

        private void CloseTimer_Tick(object? sender, EventArgs e)
        {
            _closeTimer.Stop();

            // Start fade out animation — use clone to avoid handler accumulation on shared resource
            var fadeOut = ((Storyboard)FindResource("FadeOut")).Clone();
            fadeOut.Completed += FadeOut_Completed;
            fadeOut.Begin(this);
        }

        private void FadeOut_Completed(object? sender, EventArgs e)
        {
            this.Close();
        }

        public static void ShowNotification()
        {
            var window = new BackupNotificationWindow();
            window.Show();
        }
    }
}

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace AutoDescriptionLocalizer
{
    public partial class ProgressOverlay : Window
    {
        private readonly Window ownerWindow;
        private double collapsedWidth = 260;
        private double expandedWidth = 260;
        private bool isExpanded = false;

        public ProgressOverlay(Window owner = null)
        {
            InitializeComponent();

            ownerWindow = owner ?? Application.Current?.MainWindow;
            Owner = ownerWindow;
            this.ShowActivated = false;
            this.Focusable = false;

            // click-through-ish behavior: don't steal focus
            this.Loaded += (s, e) => PositionWindow();
            if (ownerWindow != null)
            {
                ownerWindow.LocationChanged += (s, e) => PositionWindow();
                ownerWindow.SizeChanged += (s, e) => PositionWindow();
            }

            // expand on hover to show full text
            this.MouseEnter += ProgressOverlay_MouseEnter;
            this.MouseLeave += ProgressOverlay_MouseLeave;
        }

        private void ProgressOverlay_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!isExpanded) return;
            AnimateWidth(expandedWidth, collapsedWidth);
            isExpanded = false;
            StatusText.MaxWidth = 180;
        }

        private void ProgressOverlay_MouseEnter(object sender, MouseEventArgs e)
        {
            if (isExpanded) return;
            AnimateWidth(collapsedWidth, expandedWidth);
            isExpanded = true;
            StatusText.MaxWidth = 340;
        }

        private void AnimateWidth(double from, double to)
        {
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
            this.BeginAnimation(Window.WidthProperty, anim);
        }

        private void PositionWindow()
        {
            try
            {
                if (ownerWindow == null) return;
                // Position at bottom-right corner, with small margin
                var margin = 12;
                var ownerLeft = ownerWindow.Left;
                var ownerTop = ownerWindow.Top;
                var ownerWidth = ownerWindow.Width;
                var ownerHeight = ownerWindow.Height;

                // convert to screen coords
                this.Left = ownerLeft + Math.Max(0, ownerWidth - this.Width - margin);
                this.Top = ownerTop + Math.Max(0, ownerHeight - this.Height - margin);
            }
            catch
            {
                // Ignore positioning failures
            }
        }

        public void SetProgress(double percent, string statusShort)
        {
            // Keep UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                Pb.Value = Math.Max(0, Math.Min(100, percent));
                StatusText.Text = statusShort ?? "";
            });
        }
    }
}

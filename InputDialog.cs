using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameLauncher
{
    static class InputDialog
    {
        public static string Ask(Window owner, string title, string prompt, string initial)
        {
            var w = new Window
            {
                Title = title,
                Owner = owner,
                Width = 440,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)),
                Foreground = (Brush)Application.Current.Resources["TextBrush"]
            };
            var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 10, 0, 18) };
            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            string result = null;
            ok.Click += (s, e) => { result = box.Text; w.DialogResult = true; };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SecondaryBrush"] });
            root.Children.Add(box);
            root.Children.Add(buttons);
            w.Content = root;

            w.SourceInitialized += (s, e) => Native.DarkTitleBar(new WindowInteropHelper(w).Handle);
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? result : null;
        }
    }
}

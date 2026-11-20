using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GameLauncher
{
    static class InputDialog
    {
        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        static Window MakeWindow(Window owner, string title) => new Window
        {
            Title = title,
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)),
            Foreground = Res("TextBrush")
        };

        static StackPanel Buttons(Button ok, Button cancel)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            p.Children.Add(ok);
            p.Children.Add(cancel);
            return p;
        }

        public static string Ask(Window owner, string title, string prompt, string initial)
        {
            var w = MakeWindow(owner, title);
            var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 10, 0, 18) };
            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            string result = null;
            ok.Click += (s, e) => { result = box.Text; w.DialogResult = true; };

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = Res("SecondaryBrush") });
            root.Children.Add(box);
            root.Children.Add(Buttons(ok, cancel));
            w.Content = root;

            w.SourceInitialized += (s, e) => Native.DarkTitleBar(new WindowInteropHelper(w).Handle);
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? result : null;
        }

        /// <summary>"Delete this shortcut?" with the file path and a red Delete button. Cancel is the default
        /// (Enter / Esc), so a stray key press never deletes. True = delete.</summary>
        public static bool ConfirmDelete(Window owner, string name, string path)
        {
            var w = MakeWindow(owner, "Delete");
            var delete = new Button { Content = "Delete", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true, MinWidth = 90 };
            delete.Loaded += (s, e) =>
            {
                // Red button: set after the theme applied its template.
                delete.Background = Res("DangerBrush");
                delete.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x10, 0x10));
                delete.BorderBrush = Res("DangerBrush");
            };
            bool result = false;
            delete.Click += (s, e) => { result = true; w.DialogResult = true; };

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = $"Delete {name}?",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
            root.Children.Add(new TextBlock
            {
                Text = "This file is moved to the Recycle Bin and disappears from the launcher. "
                     + "Only the shortcut is removed: the game itself stays installed.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("SecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text = path,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = Res("MutedBrush"),
                Margin = new Thickness(0, 0, 0, 18)
            });
            root.Children.Add(Buttons(delete, cancel));
            w.Content = root;

            w.SourceInitialized += (s, e) => Native.DarkTitleBar(new WindowInteropHelper(w).Handle);
            w.Loaded += (s, e) => cancel.Focus();
            return w.ShowDialog() == true && result;
        }

        public static List<string> Parse(string text) =>
            (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Tag editor: a text box plus clickable pills for every tag already in use. Returns null on cancel.</summary>
        public static List<string> AskTags(Window owner, string gameName, IList<string> current, IEnumerable<string> existing)
        {
            var w = MakeWindow(owner, "Tags");
            var all = existing.Concat(current).Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();

            var box = new TextBox { Text = string.Join(", ", current), Margin = new Thickness(0, 8, 0, 18) };
            var pills = new WrapPanel();
            var pillFor = new Dictionary<string, (Border b, TextBlock icon, TextBlock text)>(StringComparer.OrdinalIgnoreCase);
            var accentBg = new SolidColorBrush(Color.FromArgb(0x33, 0x60, 0xCD, 0xFF));
            var idleBg = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            var hoverBg = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            var idleBorder = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

            void Refresh()
            {
                var on = new HashSet<string>(Parse(box.Text), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in pillFor)
                {
                    bool sel = on.Contains(kv.Key);
                    var (b, icon, text) = kv.Value;
                    b.Background = sel ? accentBg : (b.IsMouseOver ? hoverBg : idleBg);
                    b.BorderBrush = sel ? Res("AccentBrush") : idleBorder;
                    text.Foreground = sel ? Res("AccentBrush") : Res("SecondaryBrush");
                    icon.Text = sel ? "" : ""; // check / add
                    icon.Foreground = text.Foreground;
                }
            }

            void Toggle(string tag)
            {
                var list = Parse(box.Text);
                int i = list.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) list.RemoveAt(i); else list.Add(tag);
                box.Text = string.Join(", ", list);
                box.CaretIndex = box.Text.Length;
            }

            foreach (var tag in all)
            {
                var icon = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var text = new TextBlock { Text = tag, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(icon);
                sp.Children.Add(text);
                var b = new Border
                {
                    Child = sp,
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 4, 12, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to add or remove"
                };
                b.MouseLeftButtonUp += (s, e) => { Toggle(tag); e.Handled = true; };
                b.MouseEnter += (s, e) => Refresh();
                b.MouseLeave += (s, e) => Refresh();
                pillFor[tag] = (b, icon, text);
                pills.Children.Add(b);
            }
            box.TextChanged += (s, e) => Refresh();
            Refresh();

            var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            List<string> result = null;
            ok.Click += (s, e) => { result = Parse(box.Text); w.DialogResult = true; };

            var root = new StackPanel { Margin = new Thickness(20) };
            root.Children.Add(new TextBlock
            {
                Text = $"Tags for {gameName}",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            root.Children.Add(new TextBlock
            {
                Text = all.Count > 0 ? "Click a tag to add or remove it, or type new ones separated by commas."
                                     : "Type tags separated by commas (for example: horror, coop, finished).",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("SecondaryBrush"),
                Margin = new Thickness(0, 0, 0, 12)
            });
            if (all.Count > 0)
                root.Children.Add(new ScrollViewer
                {
                    Content = pills,
                    MaxHeight = 190,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            root.Children.Add(box);
            root.Children.Add(Buttons(ok, cancel));
            w.Content = root;

            w.SourceInitialized += (s, e) => Native.DarkTitleBar(new WindowInteropHelper(w).Handle);
            w.Loaded += (s, e) => { box.Focus(); box.CaretIndex = box.Text.Length; };
            return w.ShowDialog() == true ? result : null;
        }
    }
}

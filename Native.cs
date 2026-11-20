using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher
{
    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        /// <summary>Moves one file to the Recycle Bin (restorable), without Explorer's own prompts.
        /// Returns null on success, otherwise an error message.</summary>
        public static string RecycleFile(string path)
        {
            const uint FO_DELETE = 3;
            const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
            if (!System.IO.File.Exists(path)) return "The file no longer exists.";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0", // double-null-terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            int rc = SHFileOperation(ref op);
            if (rc != 0) return $"Windows couldn't delete it (error 0x{rc:X}). It may be in use or read-only.";
            if (op.fAnyOperationsAborted) return "The delete was cancelled.";
            return System.IO.File.Exists(path) ? "The file is still there." : null;
        }

        // ---- window placement (multi-monitor aware: real pixels on the whole desktop) ----
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT minPosition, maxPosition;
            public RECT normalPosition;
        }
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);

        /// <summary>The window's normal (not maximized) bounds as [left, top, right, bottom] in screen pixels,
        /// which also pins down the monitor it's on.</summary>
        public static int[] GetNormalBounds(IntPtr hwnd)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return null;
            var r = wp.normalPosition;
            return r.Right > r.Left && r.Bottom > r.Top ? new[] { r.Left, r.Top, r.Right, r.Bottom } : null;
        }

        /// <summary>Puts a not-yet-shown window at saved normal bounds, if they are still on a connected monitor
        /// (most of the title bar must be on one). Doesn't show the window. False = bounds unusable.</summary>
        public static bool SetNormalBounds(IntPtr hwnd, int[] b)
        {
            if (b == null || b.Length != 4 || b[2] - b[0] < 100 || b[3] - b[1] < 100) return false;
            var titleBar = new RECT { Left = b[0] + 40, Top = b[1], Right = b[2] - 40, Bottom = b[1] + 40 };
            const uint MONITOR_DEFAULTTONULL = 0;
            if (MonitorFromRect(ref titleBar, MONITOR_DEFAULTTONULL) == IntPtr.Zero) return false; // monitor gone
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return false;
            wp.showCmd = 0; // SW_HIDE: just move it; WPF shows it (normal or maximized) afterwards
            wp.flags = 0;
            wp.normalPosition = new RECT { Left = b[0], Top = b[1], Right = b[2], Bottom = b[3] };
            return SetWindowPlacement(hwnd, ref wp);
        }

        public static void RemoveSysMenu(IntPtr hwnd)
        {
            const int GWL_STYLE = -16; const long WS_SYSMENU = 0x00080000;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
        }

        /// <summary>Windows 11 rounded corners (and the system's soft border) for a popup such as the tray menu.</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            int round = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }

        /// <summary>Windows 11 22H2+ Mica backdrop. Returns false on older systems.</summary>
        public static bool TryMica(IntPtr hwnd)
        {
            DarkTitleBar(hwnd);
            if (Environment.OSVersion.Version.Build < 22621) return false;
            int mica = 2; // DWMSBT_MAINWINDOW
            return DwmSetWindowAttribute(hwnd, 38, ref mica, 4) == 0; // DWMWA_SYSTEMBACKDROP_TYPE
        }

        public static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var raw in text.Split('+'))
            {
                var p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= 0x2; break;
                    case "alt": mods |= 0x1; break;
                    case "shift": mods |= 0x4; break;
                    case "win": case "windows": mods |= 0x8; break;
                    default:
                        if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                        if (!Enum.TryParse<Key>(p, true, out var key)) return false;
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                        break;
                }
            }
            return vk != 0;
        }
    }

    /// <summary>Large shell icons (same ones Explorer shows), extracted on a dedicated STA thread.</summary>
    static class Icons
    {
        static readonly BlockingCollection<(string path, int size, TaskCompletionSource<ImageSource> tcs)> Queue = new();

        static Icons()
        {
            var t = new Thread(() =>
            {
                foreach (var job in Queue.GetConsumingEnumerable())
                {
                    ImageSource img = null;
                    try { img = ShellImage.Get(job.path, job.size); } catch { }
                    job.tcs.TrySetResult(img);
                }
            }) { IsBackground = true, Name = "IconLoader" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public static Task<ImageSource> GetAsync(string path, int size)
        {
            var tcs = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queue.Add((path, size, tcs));
            return tcs.Task;
        }
    }

    static class ShellImage
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint b0, b1, b2; public IntPtr dshSection; public uint dsOffset; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, ref DIBSECTION ds);

        public static ImageSource Get(string path, int size)
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try
            {
                // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
                try { return FromHBitmap(hbm); }
                finally { DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        static BitmapSource FromHBitmap(IntPtr hbm)
        {
            var ds = new DIBSECTION();
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var buf = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);

            if (ds.dsBmih.biHeight > 0) // bottom-up DIB: flip rows
            {
                var flipped = new byte[buf.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
                buf = flipped;
            }

            bool hasAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { hasAlpha = true; break; }

            var bmp = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }
    }
}

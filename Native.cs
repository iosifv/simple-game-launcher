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

        public static void RemoveSysMenu(IntPtr hwnd)
        {
            const int GWL_STYLE = -16; const long WS_SYSMENU = 0x00080000;
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
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

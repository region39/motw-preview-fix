// PreviewMotwHandlers.cs
// Managed Windows shell preview handler: raster images, PSD/PSB composites,
// SVG (via IE WebBrowser control), EPS/AI/PS (via Ghostscript).
//
// Build (no SDK required, csc ships with .NET Framework 4.x):
//   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:library /platform:x64
//     /out:PreviewMotwHandlers.dll
//     /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//     PreviewMotwHandlers.cs
//
// Register:
//   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\regasm.exe /codebase PreviewMotwHandlers.dll
//   (install.ps1 does the build, registration and shell associations)
//
// Source files must be saved as UTF-8 with BOM: csc.exe derives the source
// encoding from the BOM.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: ComVisible(true)]
[assembly: Guid("9A3C71B4-2D5F-4E88-8C1A-6B0E5F2D7A31")]

namespace PreviewMotw
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public int time;
        public int ptX;
        public int ptY;
    }

    [ComImport, Guid("8895B1C6-B41F-4C1C-A562-0D564250836F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int SW_SHOW = 5;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool repaint);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }

    /// <summary>State of the last DoPreview call - used by TestHarness and by troubleshooting.</summary>
    public static class Diagnostics
    {
        public static string Last = "";
        public static void Set(string s) { Last = s; }
    }

    [ComVisible(true)]
    [Guid("B5C1D3E7-4A62-4F0F-9C1E-7D2A5B8F3C64")]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("PreviewMotw.ImageHandler")]
    public class ImagePreviewHandler : IPreviewHandler, IInitializeWithFile, IOleWindow, IDisposable
    {
        private string _filePath;
        private IntPtr _parentHwnd = IntPtr.Zero;
        private RECT _rect;
        private Panel _host;
        private Control _content;
        private bool _disposed;

        public void Initialize(string pszFilePath, uint grfMode)
        {
            _filePath = pszFilePath;
        }

        public void SetWindow(IntPtr hwnd, ref RECT rect)
        {
            _parentHwnd = hwnd;
            _rect = rect;
            DestroyHost();
        }

        public void SetRect(ref RECT rect)
        {
            _rect = rect;
            if (_host != null)
            {
                MoveHost();
                _host.PerformLayout();
            }
        }

        public void DoPreview()
        {
            try
            {
                Diagnostics.Set("start file=" + _filePath + " parent=" + _parentHwnd);
                if (_parentHwnd == IntPtr.Zero || string.IsNullOrEmpty(_filePath))
                {
                    Diagnostics.Set("abort: no parent window or no file");
                    return;
                }
                DestroyHost();
                EnsureHost();
                LoadContent();
            }
            catch (Exception ex)
            {
                Diagnostics.Set("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
                ShowMessage("Preview failed:\r\n" + ex.Message, Color.Firebrick);
            }
        }

        public void Unload() { DestroyHost(); }

        public void SetFocus() { if (_host != null) _host.Focus(); }

        public void QueryFocus(out IntPtr phwnd) { phwnd = _host != null ? _host.Handle : IntPtr.Zero; }

        public int TranslateAccelerator(ref MSG pmsg) { return 1; }

        public void GetWindow(out IntPtr phwnd) { phwnd = _host != null ? _host.Handle : IntPtr.Zero; }

        public void ContextSensitiveHelp(bool fEnterMode) { }

        private void EnsureHost()
        {
            _host = new Panel();
            _host.BackColor = Color.White;
            _host.BorderStyle = BorderStyle.None;
            IntPtr h = _host.Handle;

            Native.SetParent(h, _parentHwnd);
            long s = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
            s |= Native.WS_CHILD;
            s &= ~(long)Native.WS_POPUP;
            Native.SetWindowLongPtr(h, Native.GWL_STYLE, new IntPtr(s));

            MoveHost();
            Native.ShowWindow(h, Native.SW_SHOW);
        }

        private void MoveHost()
        {
            if (_host == null || !_host.IsHandleCreated) return;
            int w = Math.Max(1, _rect.Right - _rect.Left);
            int h = Math.Max(1, _rect.Bottom - _rect.Top);
            Native.MoveWindow(_host.Handle, 0, 0, w, h, true);
        }

        private void DestroyHost()
        {
            try
            {
                if (_content != null) { _content.Dispose(); _content = null; }
                if (_host != null) { _host.Dispose(); _host = null; }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DestroyHost();
        }

        private void LoadContent()
        {
            string ext = Path.GetExtension(_filePath).ToLowerInvariant();
            Image img = null;

            if (ext == ".svg" || ext == ".svgz") { ShowWeb(); return; }

            if (ext == ".psd" || ext == ".psb")
            {
                try { img = PsdReader.GetPreview(_filePath); } catch (Exception ex) { Diagnostics.Set("psd error " + ex.Message); }
            }

            if (img == null && (ext == ".eps" || ext == ".ai" || ext == ".ps"))
            {
                try { img = GhostscriptRenderer.Render(_filePath); } catch (Exception ex) { Diagnostics.Set("gs error " + ex.Message); }
            }

            if (img == null)
            {
                try
                {
                    using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var src = Image.FromStream(fs))
                    {
                        img = new Bitmap(src);
                    }
                }
                catch (Exception ex) { Diagnostics.Set("gdi error " + ex.Message); }
            }

            if (img != null)
            {
                Diagnostics.Set("image " + img.Width + "x" + img.Height + " ext=" + ext);
                ShowImage(img);
                return;
            }

            Diagnostics.Set("no content ext=" + ext);
            ShowMessage("No preview available for " + ext +
                        ((ext == ".eps" || ext == ".ai" || ext == ".ps") ? "\r\n(Ghostscript is required for PostScript)" : ""),
                        Color.DimGray);
        }

        private void ShowImage(Image img)
        {
            var pb = new PictureBox();
            pb.Dock = DockStyle.Fill;
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            pb.BackColor = Color.White;
            pb.Image = img;
            AddContent(pb);
        }

        private void ShowWeb()
        {
            var wb = new WebBrowser();
            wb.Dock = DockStyle.Fill;
            wb.ScriptErrorsSuppressed = true;
            wb.AllowNavigation = true;
            wb.IsWebBrowserContextMenuEnabled = false;
            AddContent(wb);
            try
            {
                wb.Navigate(new Uri(_filePath).AbsoluteUri);
                Diagnostics.Set("web navigated");
            }
            catch (Exception ex) { Diagnostics.Set("web error " + ex.Message); }
        }

        private void ShowMessage(string text, Color color)
        {
            if (_host == null) return;
            var lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.ForeColor = color;
            lbl.BackColor = Color.White;
            lbl.Text = text;
            AddContent(lbl);
        }

        private void AddContent(Control c)
        {
            if (_host == null) return;
            _content = c;
            _host.Controls.Add(c);
            c.BringToFront();
            c.Visible = true;
            _host.PerformLayout();
        }
    }

    /// <summary>PSD/PSB reader: full composite first, embedded JPEG thumbnail as a fallback.</summary>
    internal static class PsdReader
    {
        // PSD/PSB is a big-endian format.
        private static ushort BE16(byte[] b, int i) { return (ushort)((b[i] << 8) | b[i + 1]); }

        private static uint BE32(byte[] b, int i)
        {
            return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
        }

        private static byte[] Read(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int k = s.Read(buf, off, n - off);
                if (k <= 0) break;
                off += k;
            }
            return buf;
        }

        public static Image GetPreview(string path)
        {
            try { var im = GetComposite(path); if (im != null) return im; } catch { }
            try { return GetThumbnail(path); } catch { return null; }
        }

        private static int UnpackBits(byte[] src, byte[] dst, int dstOffset, int count)
        {
            int i = 0, o = dstOffset;
            int end = dstOffset + count;
            while (i < src.Length && o < end)
            {
                sbyte n = (sbyte)src[i++];
                if (n >= 0)
                {
                    int len = Math.Min(n + 1, Math.Min(src.Length - i, end - o));
                    if (len <= 0) break;
                    Array.Copy(src, i, dst, o, len);
                    i += len; o += len;
                }
                else if (n != -128)
                {
                    int len = 1 - n;
                    if (i >= src.Length) break;
                    byte v = src[i++];
                    for (int k = 0; k < len && o < end; k++) dst[o++] = v;
                }
            }
            return o - dstOffset;
        }

        private static Image GetComposite(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var hdr = Read(fs, 26);
                if (hdr.Length < 26 || hdr[0] != '8' || hdr[1] != 'B' || hdr[2] != 'P' || hdr[3] != 'S') return null;
                int channels = BE16(hdr, 12);
                int height = (int)BE32(hdr, 14);
                int width = (int)BE32(hdr, 18);
                int depth = BE16(hdr, 22);
                int mode = BE16(hdr, 24);
                if (depth != 8 || width < 1 || height < 1 || width > 12000 || height > 12000) return null;
                if (channels < 1 || channels > 56) return null;
                if ((long)width * height > 40000000L) return null;

                uint cmLen = BE32(Read(fs, 4), 0); fs.Position += cmLen;
                uint resLen = BE32(Read(fs, 4), 0); fs.Position += resLen;
                uint lmLen = BE32(Read(fs, 4), 0); fs.Position += lmLen;

                var compBuf = Read(fs, 2);
                if (compBuf.Length < 2) return null;
                int compression = BE16(compBuf, 0);

                int planeBytes = width * height;
                var planes = new byte[channels][];

                if (compression == 0)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        planes[c] = Read(fs, planeBytes);
                        if (planes[c].Length < planeBytes) return null;
                    }
                }
                else if (compression == 1)
                {
                    int total = channels * height;
                    var countsBuf = Read(fs, total * 2);
                    if (countsBuf.Length < total * 2) return null;
                    var rowCounts = new int[total];
                    for (int i = 0; i < total; i++) rowCounts[i] = BE16(countsBuf, i * 2);

                    for (int c = 0; c < channels; c++)
                    {
                        planes[c] = new byte[planeBytes];
                        for (int y = 0; y < height; y++)
                        {
                            int count = rowCounts[c * height + y];
                            var rowEnc = Read(fs, count);
                            if (rowEnc.Length < count) return null;
                            UnpackBits(rowEnc, planes[c], y * width, width);
                        }
                    }
                }
                else return null; // ZIP compression is not supported -> thumbnail fallback

                var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    var buf = new byte[stride * height];
                    bool cmyk = (mode == 4 && channels >= 4);
                    for (int y = 0; y < height; y++)
                    {
                        int rowOff = y * stride;
                        int srcOff = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int o = rowOff + x * 3;
                            if (cmyk)
                            {
                                int c1 = planes[0][srcOff + x], m1 = planes[1][srcOff + x];
                                int yy = planes[2][srcOff + x], k1 = planes[3][srcOff + x];
                                buf[o + 2] = (byte)(255 - Math.Min(255, c1 + k1));
                                buf[o + 1] = (byte)(255 - Math.Min(255, m1 + k1));
                                buf[o] = (byte)(255 - Math.Min(255, yy + k1));
                            }
                            else if (channels >= 3)
                            {
                                buf[o + 2] = planes[0][srcOff + x];
                                buf[o + 1] = planes[1][srcOff + x];
                                buf[o] = planes[2][srcOff + x];
                            }
                            else
                            {
                                byte v = planes[0][srcOff + x];
                                buf[o] = v; buf[o + 1] = v; buf[o + 2] = v;
                            }
                        }
                    }
                    Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
        }

        public static Image GetThumbnail(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var hdr = Read(fs, 26);
                if (hdr.Length < 26) return null;
                if (hdr[0] != '8' || hdr[1] != 'B' || hdr[2] != 'P' || hdr[3] != 'S') return null;

                uint colorModeLen = BE32(Read(fs, 4), 0);
                fs.Position += colorModeLen;
                uint resLen = BE32(Read(fs, 4), 0);
                long resEnd = fs.Position + resLen;

                while (fs.Position + 12 <= resEnd)
                {
                    var rsig = Read(fs, 4);
                    if (rsig.Length < 4) break;
                    if (rsig[0] != '8' || rsig[1] != 'B' || rsig[2] != 'I' || rsig[3] != 'M') break;
                    ushort id = BE16(Read(fs, 2), 0);
                    int nameLen = Read(fs, 1)[0];
                    fs.Position += nameLen;
                    if ((1 + nameLen) % 2 != 0) fs.Position += 1;
                    uint size = BE32(Read(fs, 4), 0);
                    long dataStart = fs.Position;

                    // 1036 = thumbnail resource (JPEG), 28 byte header + JPEG data
                    if (id == 1036 && dataStart + 28 <= resEnd)
                    {
                        var th = Read(fs, 28);
                        uint fmt = BE32(th, 0);
                        uint compressed = BE32(th, 20);
                        long avail = dataStart + size - fs.Position;
                        int take = (int)Math.Min(compressed, (uint)Math.Max(0, avail));
                        if (fmt == 1 && take > 4)
                        {
                            var jpeg = Read(fs, take);
                            try
                            {
                                using (var ms = new MemoryStream(jpeg))
                                using (var im = Image.FromStream(ms))
                                {
                                    return new Bitmap(im);
                                }
                            }
                            catch { }
                        }
                    }

                    fs.Position = dataStart + size + (size % 2);
                }
                return null;
            }
        }
    }

    /// <summary>Rasterizes the first page of EPS / AI / PS with Ghostscript.</summary>
    internal static class GhostscriptRenderer
    {
        private static string _gsPath;
        private static bool _searched;

        public static string FindGhostscript()
        {
            if (_searched) return _gsPath;
            _searched = true;

            foreach (var root in new[] { @"C:\Program Files\gs", @"C:\Program Files (x86)\gs" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var dir in Directory.GetDirectories(root, "gs*"))
                    {
                        foreach (var exe in new[] { "gswin64c.exe", "gswin32c.exe" })
                        {
                            string p = Path.Combine(dir, "bin", exe);
                            if (File.Exists(p)) { _gsPath = p; return _gsPath; }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public static Image Render(string file)
        {
            string gs = FindGhostscript();
            if (gs == null) return null;

            string outPng = Path.Combine(Path.GetTempPath(), "motw-prev-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = gs;
                psi.Arguments = "-q -dNOPAUSE -dBATCH -dSAFER -sDEVICE=png16m -r120 -dFirstPage=1 -dLastPage=1 -sOutputFile=\"" + outPng + "\" \"" + file + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
                }
                if (!File.Exists(outPng)) return null;
                using (var fs = new FileStream(outPng, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var im = Image.FromStream(fs))
                {
                    return new Bitmap(im);
                }
            }
            catch { return null; }
            finally { try { if (File.Exists(outPng)) File.Delete(outPng); } catch { } }
        }
    }
}

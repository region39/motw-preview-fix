// PreviewProbe.cs - рендерит файл произвольным shell preview handler и снимает скриншот.
// Build: csc /target:exe /platform:x64 /out:PreviewProbe.exe PreviewProbe.cs
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

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

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public int time; public int ptX; public int ptY; }

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: PreviewProbe.exe <clsid> <file> <out.png>"); return 2; }
        string clsid = args[0], file = args[1], outPng = args[2];

        Application.EnableVisualStyles();
        var form = new Form();
        form.Text = "preview probe";
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.Size = new Size(900, 700);
        form.TopMost = true;
        var panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = Color.White;
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();

        try
        {
            Type t = Type.GetTypeFromCLSID(new Guid(clsid), true);
            object obj = Activator.CreateInstance(t);
            ((IInitializeWithFile)obj).Initialize(file, 0);
            var ph = (IPreviewHandler)obj;
            RECT r;
            r.Left = 0; r.Top = 0; r.Right = panel.ClientSize.Width; r.Bottom = panel.ClientSize.Height;
            ph.SetWindow(panel.Handle, ref r);
            ph.DoPreview();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }

            using (var bmp = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    Point sp = panel.PointToScreen(Point.Empty);
                    g.CopyFromScreen(sp, Point.Empty, panel.ClientSize);
                }
                bmp.Save(outPng, ImageFormat.Png);
            }
            ph.Unload();
            Console.WriteLine("OK " + outPng);
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
            form.Close();
            return 1;
        }
        form.Close();
        return 0;
    }
}

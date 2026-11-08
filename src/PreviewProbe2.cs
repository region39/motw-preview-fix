// PreviewProbe2.cs - рендерит файл произвольным preview handler (file/stream/item) + скриншот.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public int time; public int ptX; public int ptY; }

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

[ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream pstream, uint grfMode);
}

[ComImport, Guid("7F73BE3F-FB79-493C-A6C7-7EE14E245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithItem
{
    void Initialize(object psi, uint grfMode);
}

internal static class Program
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IStream pstmTemplate, out IStream ppstm);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    private static void Step(string name, Action a)
    {
        try { a(); Console.WriteLine("  step ok: " + name); }
        catch (Exception ex) { Console.WriteLine("  step FAIL: " + name + " :: " + ex.GetType().Name + ": " + ex.Message); throw; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: PreviewProbe2.exe <clsid> <file> <out.png> [mode: file|stream|item]"); return 2; }
        string clsid = args[0], file = args[1], outPng = args[2];
        string mode = args.Length > 3 ? args[3] : "auto";

        Application.EnableVisualStyles();
        var form = new Form();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.Size = new Size(900, 700);
        form.TopMost = true;
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();

        try
        {
            object obj = null;
            Type t = null;
            Step("GetTypeFromCLSID", () => { t = Type.GetTypeFromCLSID(new Guid(clsid), true); });
            Step("CreateInstance", () => { obj = Activator.CreateInstance(t); });

            bool inited = false;
            if (!inited && (mode == "auto" || mode == "file"))
            {
                var f = obj as IInitializeWithFile;
                if (f != null) { f.Initialize(file, 0); inited = true; Console.WriteLine("  init: IInitializeWithFile"); }
            }
            if (!inited && (mode == "auto" || mode == "stream"))
            {
                var s = obj as IInitializeWithStream;
                if (s != null)
                {
                    IStream st;
                    SHCreateStreamOnFileEx(file, 0, 0, false, null, out st);
                    s.Initialize(st, 0);
                    inited = true;
                    Console.WriteLine("  init: IInitializeWithStream");
                }
            }
            if (!inited && (mode == "auto" || mode == "item"))
            {
                var it = obj as IInitializeWithItem;
                if (it != null)
                {
                    Guid shellItemIid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    object psi;
                    SHCreateItemFromParsingName(file, IntPtr.Zero, ref shellItemIid, out psi);
                    it.Initialize(psi, 0);
                    inited = true;
                    Console.WriteLine("  init: IInitializeWithItem");
                }
            }
            if (!inited) { Console.WriteLine("  NO SUPPORTED INIT INTERFACE"); form.Close(); return 3; }

            var ph = (IPreviewHandler)obj;
            RECT r;
            r.Left = 0; r.Top = 0; r.Right = panel.ClientSize.Width; r.Bottom = panel.ClientSize.Height;
            Step("SetWindow", () => ph.SetWindow(panel.Handle, ref r));
            Step("DoPreview", () => ph.DoPreview());

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
            Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
            form.Close();
            return 1;
        }
        form.Close();
        return 0;
    }
}

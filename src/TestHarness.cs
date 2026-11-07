// TestHarness.cs
// Headless check of the registered preview handler: creates a hidden window,
// hands its handle to the handler, renders the file and saves a PNG screenshot.
//
// Build:
//   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:exe /platform:x64
//     /out:TestHarness.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll
//     /r:System.Windows.Forms.dll /r:PreviewMotwHandlers.dll TestHarness.cs
//
// Usage:
//   TestHarness.exe <file> <out.png>
//
// The harness prints the handler diagnostics and writes <out.png> and <out.screen.png>.
// "image WxH ext=..." means the handler produced a bitmap - that is the success criterion.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PreviewMotw;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: TestHarness.exe <file> <out.png>");
            return 2;
        }
        string file = args[0], outPng = args[1];

        Application.EnableVisualStyles();
        var form = new Form();
        form.Text = "preview test";
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(80, 80);
        form.Size = new Size(760, 600);
        form.TopMost = true;
        var panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = Color.White;
        form.Controls.Add(panel);
        form.Show();
        Application.DoEvents();

        int rc = 0;
        try
        {
            Type t = Type.GetTypeFromCLSID(new Guid("B5C1D3E7-4A62-4F0F-9C1E-7D2A5B8F3C64"), true);
            object obj = Activator.CreateInstance(t);
            ((IInitializeWithFile)obj).Initialize(file, 0);

            var ph = (IPreviewHandler)obj;
            RECT r;
            r.Left = 0; r.Top = 0; r.Right = panel.ClientSize.Width; r.Bottom = panel.ClientSize.Height;
            ph.SetWindow(panel.Handle, ref r);
            ph.DoPreview();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(25);
            }

            Console.WriteLine("diag: " + Diagnostics.Last);

            IntPtr hchild;
            ((IOleWindow)obj).GetWindow(out hchild);
            Console.WriteLine("handler window: " + hchild + " style=0x" + GetWindowLong(hchild, -16).ToString("X8"));

            using (var bmp = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height))
            {
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(outPng, ImageFormat.Png);
            }
            using (var bmp2 = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height))
            {
                using (var g = Graphics.FromImage(bmp2))
                {
                    Point sp = panel.PointToScreen(Point.Empty);
                    g.CopyFromScreen(sp, Point.Empty, panel.ClientSize);
                }
                bmp2.Save(System.IO.Path.ChangeExtension(outPng, ".screen.png"), ImageFormat.Png);
            }
            ph.Unload();
            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            rc = 1;
        }
        form.Close();
        return rc;
    }
}

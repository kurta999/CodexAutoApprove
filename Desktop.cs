using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexAutoApprove;

internal static class Desktop
{
    public static Bitmap Capture(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static void Click(Point point)
    {
        // Move and click in physical virtual-desktop coordinates, including negative monitor origins.
        Rectangle desktop = SystemInformation.VirtualScreen;
        int absoluteX = (int)Math.Round((point.X - desktop.Left) * 65535.0 / Math.Max(1, desktop.Width - 1));
        int absoluteY = (int)Math.Round((point.Y - desktop.Top) * 65535.0 / Math.Max(1, desktop.Height - 1));
        var inputs = new[]
        {
            new Input { Type = 0, Mouse = new MouseInput { X = absoluteX, Y = absoluteY, Flags = 0x8000 | 0x4000 | 0x0001 } },
            new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } },
            new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not deliver the approval click.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public MouseInput Mouse; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}

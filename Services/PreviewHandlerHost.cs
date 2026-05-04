using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FileRecoveryParser.Services;

public class PreviewHandlerHost : HwndHost
{
    public static readonly DependencyProperty FilePathProperty =
        DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(PreviewHandlerHost),
            new PropertyMetadata(null, OnFilePathChanged));

    private static readonly Guid PreviewHandlerShellExId = new("8895b1c6-b41f-4c1c-a562-0d564250836f");

    private IntPtr  _hwnd;
    private object? _handler;

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PreviewHandlerHost host)
            host.LoadHandler(e.NewValue as string);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 0, 0,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (!string.IsNullOrEmpty(FilePath))
            LoadHandler(FilePath);

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        UnloadHandler();
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        ResizeHandler();
    }

    private void LoadHandler(string? filePath)
    {
        UnloadHandler();

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || _hwnd == IntPtr.Zero)
            return;

        var clsid = LookupHandlerClsid(Path.GetExtension(filePath));
        if (clsid == Guid.Empty) return;

        try
        {
            var type    = Type.GetTypeFromCLSID(clsid);
            var handler = type is not null ? Activator.CreateInstance(type) : null;
            if (handler is null) return;

            if (handler is not IInitializeWithFile initWithFile)
            {
                Marshal.ReleaseComObject(handler);
                return;
            }
            initWithFile.Initialize(filePath, 0);

            if (handler is IPreviewHandler previewHandler)
            {
                var rect = GetHostRect();
                previewHandler.SetWindow(_hwnd, ref rect);
                previewHandler.DoPreview();
                _handler = handler;
            }
            else
            {
                Marshal.ReleaseComObject(handler);
            }
        }
        catch { }
    }

    private void UnloadHandler()
    {
        if (_handler is IPreviewHandler ph)
        {
            try { ph.Unload(); } catch { }
        }
        if (_handler is not null)
        {
            Marshal.ReleaseComObject(_handler);
            _handler = null;
        }
    }

    private void ResizeHandler()
    {
        if (_handler is IPreviewHandler ph)
        {
            try { var rect = GetHostRect(); ph.SetRect(ref rect); }
            catch { }
        }
    }

    private RECT GetHostRect()
    {
        var size = RenderSize;
        return new RECT { Left = 0, Top = 0, Right = (int)size.Width, Bottom = (int)size.Height };
    }

    private static Guid LookupHandlerClsid(string extension)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(
                $@"{extension}\shellex\{{{PreviewHandlerShellExId}}}");
            if (key?.GetValue(null) is string clsidStr && Guid.TryParse(clsidStr, out var clsid))
                return clsid;
        }
        catch { }
        return Guid.Empty;
    }

    [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

    private const int WS_CHILD       = 0x40000000;
    private const int WS_VISIBLE     = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;

    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG
    {
        public IntPtr hwnd, wParam, lParam;
        public uint   message, time;
        public POINT  pt;
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT prc);
        void SetRect(ref RECT prc);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
#if NET
#pragma warning disable 8600, 8603, 8604 // Shared with the .NET Framework C# 5 launcher.
#endif

namespace DevTools.Infrastructure
{
    // Shared with the small .NET Framework launcher. Keep this compatible with C# 5.
    public static class ShellIdentity
    {
        public const string AppId = "Marvijo.DevTools";
        private static readonly Guid PropertySet = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        public static void ConfigureProcess()
        {
            if (InstalledLauncher() != null)
                Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
        }

        public static void ConfigureWindow(IntPtr window)
        {
            string launcher = InstalledLauncher();
            if (launcher == null) return;
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore properties;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out properties));
            try
            {
                Set(properties, 2, "\"" + launcher + "\"");
                Set(properties, 3, Path.Combine(Path.GetDirectoryName(launcher), "DevTools.ico") + ",0");
                Set(properties, 4, "DevTools");
                Set(properties, 5, AppId);
                Marshal.ThrowExceptionForHR(properties.Commit());
            }
            finally { Marshal.ReleaseComObject(properties); }
        }

        public static void RegisterShortcut(string path)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore properties;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out properties));
            try
            {
                Set(properties, 5, AppId);
                Marshal.ThrowExceptionForHR(properties.Commit());
            }
            finally { Marshal.ReleaseComObject(properties); }
            SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
        }

        private static string InstalledLauncher()
        {
            var version = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            if (version.Parent == null || version.Parent.Name != "versions" || version.Parent.Parent == null) return null;
            string launcher = Path.Combine(version.Parent.Parent.FullName, "DevTools.Launcher.exe");
            return File.Exists(launcher) ? launcher : null;
        }

        private static void Set(IPropertyStore store, uint id, string text)
        {
            var key = new PropertyKey { Format = PropertySet, Id = id };
            var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
            try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bind, uint flags, ref Guid iid, out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr other);
    }
}

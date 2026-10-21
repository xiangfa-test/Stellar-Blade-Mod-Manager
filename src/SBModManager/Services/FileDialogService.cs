using System.Runtime.InteropServices;

namespace SBModManager.Services;

/// <summary>
/// Win32 common file dialogs (IFileOpenDialog). Windows.Storage.Pickers does not work
/// reliably in an elevated (administrator) process, which is our default mode.
/// </summary>
public static class FileDialogService
{
    public sealed record DialogFilter(string Name, string Spec);

    private const int ErrorCancelled = unchecked((int)0x800704C7);

    [Flags]
    private enum Options : uint
    {
        ForceFileSystem = 0x00000040,
        AllNonStorageItems = 0x00000080,
        AllowMultiSelect = 0x00000200,
        PathMustExist = 0x00000800,
        FileMustExist = 0x00001000,
        PickFolders = 0x00000020,
    }

    private enum Sigdn : uint
    {
        FileSystemPath = 0x80058000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr hwndParent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        uint GetFileTypeIndex();
        uint Advise(IntPtr pfde);
        void Unadvise(uint dwCookie);
        void SetOptions(Options fos);
        Options GetOptions();
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        IShellItem GetFolder();
        IShellItem GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        IShellItem GetResult();
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        IShellItemArray GetResults();
        IShellItemArray GetSelectedItems();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(Sigdn sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    public static IReadOnlyList<string> PickFiles(IntPtr owner, string title, params DialogFilter[] filters)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogClass();
        try
        {
            dialog.SetOptions(Options.ForceFileSystem | Options.FileMustExist | Options.AllowMultiSelect);
            dialog.SetTitle(title);
            if (filters.Length > 0)
            {
                var specs = filters
                    .Select(f => new FilterSpec { Name = f.Name, Spec = f.Spec })
                    .ToArray();
                dialog.SetFileTypes((uint)specs.Length, specs);
            }

            var hr = dialog.Show(owner);
            if (hr == ErrorCancelled) return Array.Empty<string>();
            Marshal.ThrowExceptionForHR(hr);

            var results = dialog.GetResults();
            try
            {
                results.GetCount(out var count);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    results.GetItemAt(i, out var item);
                    try
                    {
                        item.GetDisplayName(Sigdn.FileSystemPath, out var ptr);
                        if (ptr != IntPtr.Zero)
                        {
                            try { paths.Add(Marshal.PtrToStringUni(ptr) ?? ""); }
                            finally { Marshal.FreeCoTaskMem(ptr); }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
                return paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            }
            finally
            {
                Marshal.ReleaseComObject(results);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    public static string? PickFolder(IntPtr owner, string title)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogClass();
        try
        {
            dialog.SetOptions(Options.ForceFileSystem | Options.PathMustExist | Options.PickFolders);
            dialog.SetTitle(title);
            var hr = dialog.Show(owner);
            if (hr == ErrorCancelled) return null;
            Marshal.ThrowExceptionForHR(hr);

            var item = dialog.GetResult();
            try
            {
                item.GetDisplayName(Sigdn.FileSystemPath, out var ptr);
                if (ptr == IntPtr.Zero) return null;
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    return string.IsNullOrWhiteSpace(path) ? null : path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using PKSoft;
using TinyWall.Interface.Internal;

public enum PathFormat
{
    NativeNt,
    Volume,
    Win32
}

public sealed class PathMapper : IDisposable
{
    [SuppressUnmanagedCodeSecurity]
    private static class NativeMethods
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int QueryDosDevice(string lpDeviceName, [Out] StringBuilder lpTargetPath, int ucchMax);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNamesForVolumeName(string lpszVolumeName, [Out] char[] lpszVolumePathNames, int cchBufferLength, out int lpcchReturnLength);
    }

    public struct DriveCache
    {
        public string Device;
        public string Volume;
        public List<string> PathNames;
    }

    private ManagementEventWatcher DriveWatcher;
    private ManualResetEvent CacheReadyEvent = new ManualResetEvent(false);
    private readonly object locker = new object();
    private bool disposed = false;

    public PathMapper()
    {
        try
        {
            // TODO: Use WM_DEVICECHANGE instead of WMI
            WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceOperationEvent WITHIN 5 WHERE Targetinstance ISA 'Win32_MountPoint'");
            DriveWatcher = new ManagementEventWatcher(insertQuery);
            DriveWatcher.EventArrived += Watcher_EventArrived;
            DriveWatcher.Start();
        }
        catch { }

        RebuildCache();
    }

    private void Watcher_EventArrived(object sender, EventArrivedEventArgs e)
    {
        RebuildCache();
    }

    private DriveCache[] _cache;
    public DriveCache[] Cache
    {
        get
        {
            DriveCache[] ret = null;
            CacheReadyEvent.WaitOne();
            lock (locker)
            {
                ret = _cache;
            }
            return ret;
        }

        private set
        {
            lock(locker)
            {
                _cache = value;
            }
            CacheReadyEvent.Set();
        }
    }

    private void RebuildCache()
    {
        ThreadPool.QueueUserWorkItem(delegate (object arg)
        {
            const int MAX_PATH = 260;

            StringBuilder sb = new StringBuilder(MAX_PATH);
            char[] buf = new char[MAX_PATH];
            List<DriveCache> newCache = new List<DriveCache>();

            foreach (var vol in FindVolumeSafeHandle.EnumerateVolumes())
            {
                var cacheEntry = new DriveCache();

                if ( (vol[0] != '\\')
                    || (vol[1] != '\\')
                    || (vol[2] != '?')
                    || (vol[3] != '\\')
                    || (vol[vol.Length - 1] != '\\') )
                {
                    continue;
                }
                cacheEntry.Volume = vol;

                string qddInput = vol.Substring(4, vol.Length - 5); // Also remove trailing backslash
                int charCount = NativeMethods.QueryDosDevice(qddInput, sb, sb.Capacity);
                if (charCount > 0)
                {
                    sb.Append('\\');
                    cacheEntry.Device = sb.ToString();
                }

                if (NativeMethods.GetVolumePathNamesForVolumeName(vol, buf, buf.Length, out int expectedChars))
                {
                    cacheEntry.PathNames = new List<string>();
                    int startIdx = 0;
                    int numChars = 0;
                    for (int i = 0; i < expectedChars; ++i)
                    {
                        if ((buf[i] == '\0') && (numChars > 0))
                        {
                            cacheEntry.PathNames.Add(new string(buf, startIdx, numChars));
                            startIdx = i + 1;
                            numChars = 0;
                        }
                        else
                            ++numChars;
                    }
                }

                newCache.Add(cacheEntry);
            }

            Cache = newCache.ToArray();
        }, null);
    }

    public string ConvertPathIgnoreErrors(string path, PathFormat target)
    {
        try
        {
            return ConvertPath(path, target);
        }
        catch
        {
            return path;
        }
    }

    public string ConvertPath(string path, PathFormat target)
    {
        string ret = path;
        StringBuilder sb = new StringBuilder();

        ret = ReplaceLeading(ret, @"\SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.System), sb);
        ret = ReplaceLeading(ret, @"\\?\", string.Empty, sb);
        ret = ReplaceLeading(ret, @"\\.\", string.Empty, sb);
        ret = ReplaceLeading(ret, @"\??\", string.Empty, sb);
        ret = ReplaceLeading(ret, @"UNC\",@"\\", sb);
        ret = ReplaceLeading(ret, @"GLOBALROOT\", string.Empty, sb);
        ret = ReplaceLeading(ret, @"\Device\Mup\", @"\\", sb);

        if (NetworkPath.IsNetworkPath(ret))
        {   // UNC path (like \\server\share\directory\file), or mounted network drive

            if (!NetworkPath.IsUncPath(ret))
            {
                // Convert a mapped drive to a UNC path
                char driveLetter = char.ToUpperInvariant(ret[0]);
                using (var networkKey = Registry.CurrentUser.OpenSubKey("Network", false))
                {
                    var subkeys = networkKey.GetSubKeyNames();
                    foreach (var sk in subkeys)
                    {
                        if ((sk.Length == 1) && (char.ToUpperInvariant(sk[0]) == driveLetter))
                        {
                            using (var driveKey = networkKey.OpenSubKey(sk, false))
                            {
                                ret = Path.Combine((string)driveKey.GetValue("RemotePath"), ret.Substring(3));
                                break;
                            }
                        }
                    }
                }

                // If conversion failed
                if (!NetworkPath.IsUncPath(ret))
                    throw new DriveNotFoundException();
            }

            switch (target)
            {
                case PathFormat.Win32:
                    return ret;
                case PathFormat.NativeNt:
                    return @"\Device\Mup\" + ret.Substring(2);
                default:
                    throw new NotSupportedException();
            }
        }
        else if ((ret.Length >= 3) && char.IsLetter(ret[0]) && (ret[1] == ':') && (ret[2] == '\\'))
        {   // Win32 drive letter format, like C:\Windows\explorer.exe

            if (target == PathFormat.Win32)
                return ret;

            var dc = Cache;
            for (int i = 0; i < dc.Length; ++i)
            {
                for (int j = 0; j < dc[i].PathNames.Count; ++j)
                {
                    if (ret.StartsWith(dc[i].PathNames[j], StringComparison.OrdinalIgnoreCase))
                    {
                        string trailing = ret.Substring(dc[i].PathNames[j].Length);
                        switch (target)
                        {
                            case PathFormat.NativeNt:
                                return Path.Combine(dc[i].Device, trailing);
                            case PathFormat.Volume:
                                return Path.Combine(dc[i].Volume, trailing);
                            default:
                                throw new NotSupportedException();
                        }
                    }
                }
            }

            throw new DriveNotFoundException();
        }
        else if (ret.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
        {   // Volume GUID path, like \\?\Volume{26a21bda-a627-11d7-9931-806e6f6e6963}\Windows\explorer.exe

            if (target == PathFormat.Volume)
                return path;

            ret = @"\\?\" + ret;
            var dc = Cache;
            for (int i = 0; i < dc.Length; ++i)
            {
                if (ret.StartsWith(dc[i].Volume, StringComparison.OrdinalIgnoreCase))
                {
                    string trailing = ret.Substring(dc[i].Volume.Length);
                    switch (target)
                    {
                        case PathFormat.NativeNt:
                            return Path.Combine(dc[i].Device, trailing);
                        case PathFormat.Win32:
                            if (dc[i].PathNames.Count > 0)
                                return Path.Combine(dc[i].PathNames[0], trailing);
                            else
                                throw new NotSupportedException();
                        default:
                            throw new NotSupportedException();
                    }
                }
            }

            throw new DriveNotFoundException();
        }
        else
        {   // Assume native NT device path, like \Device\HarddiskVolume1\Windows\explorer.exe
            if (target == PathFormat.NativeNt)
                return path;

            var dc = Cache;
            for (int i = 0; i < dc.Length; ++i)
            {
                if (ret.StartsWith(dc[i].Device, StringComparison.OrdinalIgnoreCase))
                {
                    string trailing = ret.Substring(dc[i].Device.Length);
                    switch (target)
                    {
                        case PathFormat.Volume:
                            return Path.Combine(dc[i].Volume, trailing);
                        case PathFormat.Win32:
                            if (dc[i].PathNames.Count > 0)
                                return Path.Combine(dc[i].PathNames[0], trailing);
                            else
                                throw new DriveNotFoundException();
                        default:
                            throw new NotSupportedException();
                    }
                }
            }

            throw new DriveNotFoundException();
        }
    }

    private static string ReplaceLeading(string haystack, string needle, string replacement, StringBuilder sb)
    {
        int pos = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (pos != 0)
            return haystack;
        else
        {
            sb.Length = 0;
            sb.Append(replacement);
            sb.Append(haystack, needle.Length, haystack.Length - needle.Length);
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        if (disposed) return;

        DriveWatcher?.Dispose();
        CacheReadyEvent.WaitOne();
        CacheReadyEvent.Close();

        disposed = true;
    }

#if DEBUG
    private void TestConversion(string path)
    {
        string NO_RESULT = "---";
        string win32Result = NO_RESULT;
        string ntResult = NO_RESULT;
        string volumeResult = NO_RESULT;

        try { win32Result = ConvertPath(path, PathFormat.Win32); } catch { }
        try { ntResult = ConvertPath(path, PathFormat.NativeNt); } catch { }
        try { volumeResult = ConvertPath(path, PathFormat.Volume); } catch { }

        string output = path + ":" + Environment.NewLine
            + "    Win32:  " + win32Result + Environment.NewLine
            + "    Nt:     " + ntResult + Environment.NewLine
            + "    Volume: " + volumeResult + Environment.NewLine;

        Debug.WriteLine(output);
    }

    // TODO: Automatically compare with expected outcomes
    public void RunTests()
    {
        string NETMOUNT_DRIVE = @"X:\";
        string NONEXISTENT_DRIVE = @"N:\";
        string VOLUME = @"\\?\Volume{56c747c3-83d9-11e4-91b2-806e6f6e6963}\";
        string DIR_MOUNTPOINT = @"d:\c_drive\";

        TestConversion(@"\\server\share\dir\file.txt");
        TestConversion(@"\\.\UNC\server\share\dir\file.txt");
        TestConversion(NETMOUNT_DRIVE + @"tmp");
        TestConversion(NONEXISTENT_DRIVE + @"tmp");

        TestConversion(@"c:\windows\explorer.exe");
        TestConversion(@"\\?\c:\windows\explorer.exe");
        TestConversion(DIR_MOUNTPOINT + @"windows\explorer.exe");
        TestConversion(@"\\?\UNC\c:\windows\explorer.exe");

        TestConversion(VOLUME + @"Windows\explorer.exe");
        TestConversion(@"\Device\HarddiskVolume1\Windows\explorer.exe");
        TestConversion(@"\SystemRoot\explorer.exe");
        TestConversion(@"\Device\Mup\server\share\dir\file.txt");
    }
#endif
}
﻿using System;
using System.Runtime.InteropServices;

namespace PKSoft
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll",CharSet=CharSet.Auto, CallingConvention=CallingConvention.StdCall)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, IntPtr dwExtraInfo);
        private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
        private const uint MOUSEEVENTF_LEFTUP = 0x04;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x08;
        private const uint MOUSEEVENTF_RIGHTUP = 0x10;
        internal static void DoMouseRightClick()
        {
            //Call the imported function with the cursor's current position  
            uint X = (uint)System.Windows.Forms.Cursor.Position.X;
            uint Y = (uint)System.Windows.Forms.Cursor.Position.Y;
            mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, X, Y, 0, IntPtr.Zero);
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern uint RegisterWindowMessage(string lpString);

        [DllImport("Wer.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        internal static extern void WerAddExcludedApplication(
            [MarshalAs(UnmanagedType.LPWStr)]
            string pwzExeName,
            [MarshalAs(UnmanagedType.Bool)]
            bool bAllUsers);
    }
}

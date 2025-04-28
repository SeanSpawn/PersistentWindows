﻿using System;
using System.Timers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;

using PersistentWindows.Common.WinApiBridge;
using PersistentWindows.Common.Diagnostics;
using System.Threading;

namespace PersistentWindows.Common
{
    public partial class HotKeyWindow : Form
    {
        private uint hotkey;

        public static IntPtr commanderWnd = IntPtr.Zero;
        public static bool invokedFromBrowser = false;

        private static System.Timers.Timer aliveTimer;
        private static int callerAliveTimer = -1; //for tracing the starting source of alive timer

        private System.Timers.Timer mouseScrollDelayTimer;
        private bool init = true;
        private bool active = false;
        private static bool tiny = false;
        private static bool browserWindowActivated = false;
        private static bool restoring = false;
        private int origWidth;
        private int origHeight;
        private int mouseOffset = 0; //mouse dithering to workaround mouse location mis-update issue in rdp session
        private static POINT lastCursorPos;
        private POINT lastWheelCursorPos;
        private bool handCursor = false;
        private bool ibeamCursor = false;
        private int titleHeight;
        private Color dfltBackColor;
        private bool promptZkey = true;
        private bool clickThrough = false;
        private bool defocused = false;
        private int totalWaitSecondsForWhiteColor = 0;

        public HotKeyWindow(uint hkey)
        {
            tiny = false;
            hotkey = hkey;

            InitializeComponent();

            origWidth = Width;
            origHeight = Height;

            titleHeight = this.Height - ClientRectangle.Height;

            dfltBackColor = BackColor;

            //KeyDown += new KeyEventHandler(FormKeyDown);
            KeyUp += new KeyEventHandler(FormKeyUp);
            MouseClick += new MouseEventHandler(FormMouseClick);
            MouseWheel += new MouseEventHandler(FormMouseWheel);
            MouseMove += new MouseEventHandler(FormMouseMove);
            FormClosing += new FormClosingEventHandler(FormClose);
            MouseLeave += new EventHandler(FormMouseLeave);
            SizeChanged += new EventHandler(FormSizeChanged);

            Icon = PersistentWindowProcessor.icon;

            aliveTimer = new System.Timers.Timer();
            aliveTimer.Elapsed += AliveTimerCallBack;
            aliveTimer.SynchronizingObject = this;
            aliveTimer.AutoReset = false;
            aliveTimer.Enabled = false;

            mouseScrollDelayTimer = new System.Timers.Timer();
            mouseScrollDelayTimer.Elapsed += MouseScrollCallBack;
            mouseScrollDelayTimer.AutoReset = false;
            mouseScrollDelayTimer.Enabled = false;

            commanderWnd = Handle;
        }

        private void ToggleWindowSize()
        {
            tiny = !tiny;

            if (tiny)
            {
                FormBorderStyle = FormBorderStyle.None;
                ControlBox = false;
                Width = 8;
                Height = 8;
                Location = new Point(Location.X + origWidth / 2, Location.Y + origHeight / 2);
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Fixed3D;
                ControlBox = true;
                Width = origWidth;
                Height = origHeight;
                Location = new Point(Location.X - origWidth / 2, Location.Y - origHeight / 2);
            }
        }

        private void ResetHotKeyVirtualDesktop()
        {
            //relocate HotKey window to current virtual desktop
            if (!VirtualDesktop.IsWindowOnCurrentVirtualDesktop(Handle))
            {
                IntPtr fgwnd = GetForegroundWindow();
                Guid vd = VirtualDesktop.GetWindowDesktopId(fgwnd);
                VirtualDesktop.MoveWindowToDesktop(Handle, vd);
            }
        }

        private void ResetHotkeyWindowPos(bool from_menu = false)
        {
            POINT cursor;
            User32.GetCursorPos(out cursor);
            if (from_menu)
            {
                IntPtr fgwnd = GetForegroundWindow();
                RECT fgwinPos = new RECT();
                User32.GetWindowRect(fgwnd, ref fgwinPos);
                cursor.X = (fgwinPos.Left + fgwinPos.Right) / 2;
                cursor.Y = (fgwinPos.Top + fgwinPos.Bottom) / 2;
            }
            Left = cursor.X - Size.Width / 2;
            Top = cursor.Y - Size.Height / 2;
        }

        //hack to resolve failure to repeatively set cursor pos to same value in rdp session
        private void ResetCursorPos(bool last_mouse_wheel_pos = false)
        {
            if (last_mouse_wheel_pos)
                User32.SetCursorPos(lastWheelCursorPos.X + mouseOffset, lastWheelCursorPos.Y);
            else
                User32.SetCursorPos(Left + Size.Width / 2 + mouseOffset + (handCursor ? 10 : 0), Top + Size.Height / 2);

            mouseOffset++;
            if (mouseOffset == 2)
                mouseOffset = -1;
        }

        private void SetCursorPos(POINT cursorPos)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);

            if (tiny)
            {
                Visible = false;
                return;
            }

            RECT fgwinPos = new RECT();
            User32.GetWindowRect(fgwnd, ref fgwinPos);

            RECT hkRect = new RECT();
            User32.GetWindowRect(Handle, ref hkRect);

            IntPtr cursorWnd = User32.WindowFromPoint(cursorPos);
            IntPtr cursorTopWnd = User32.GetAncestor(cursorWnd, User32.GetAncestorRoot);

            RECT intersect = new RECT();
            bool overlap = User32.IntersectRect(out intersect, ref hkRect, ref fgwinPos);
            /*
            if (overlap && (cursorWnd == Handle))
            {
                Visible = false;
            }
            */

            if (cursorTopWnd != fgwnd || !overlap)
                User32.SetCursorPos(fgwinPos.Left + fgwinPos.Width / 2, fgwinPos.Top + fgwinPos.Height / 2);
        }

        private void FormClose(object sender, FormClosingEventArgs e)
        {
            if (InvokeRequired)
                BeginInvoke((Action) delegate ()
                {
                    FormClose(sender, e);
                });
            else
            {
                e.Cancel = true;
                if (User32.IsWindow(Handle))
                {
                    Visible = false;
                    active = false;
                }
            }
        }

        private void FormMouseClick(object sender, MouseEventArgs e)
        {
            bool alt_key_pressed = (User32.GetKeyState(0x12) & 0x8000) != 0;

            if (alt_key_pressed)
                Left -= 10;
            if (clickThrough)
                Visible = false;
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            FgSleep();

            if (alt_key_pressed || clickThrough)
            {
                if (e.Button == MouseButtons.Left)
                    User32.mouse_event(MouseAction.MOUSEEVENTF_LEFTDOWN | MouseAction.MOUSEEVENTF_LEFTUP,
                        0, 0, 0, UIntPtr.Zero);
                else if (e.Button == MouseButtons.Right)
                    User32.mouse_event(MouseAction.MOUSEEVENTF_RIGHTDOWN | MouseAction.MOUSEEVENTF_RIGHTUP,
                        0, 0, 0, UIntPtr.Zero);
                if (alt_key_pressed)
                {
                    Thread.Sleep(3000);
                    Left += 10;
                }
                if (clickThrough)
                {
                    Thread.Sleep(250);
                    clickThrough = false;
                    Visible = true;
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                //page down
                SendKeys.Send("{PGDN}");
            }
            else if (e.Button == MouseButtons.Right)
            {
                //page up
                SendKeys.Send("{PGUP}");
            }
            else if (e.Button == MouseButtons.Middle)
            {
                //refresh current webpage
                SendKeys.Send("{F5}");
            }

            User32.SetForegroundWindow(Handle);
            ResetCursorPos();
        }

        private void FormMouseMove(object sender, MouseEventArgs e)
        {
            if (tiny)
                StartAliveTimer(14, 3000);
        }

        private void FormMouseWheel(object sender, MouseEventArgs e)
        {
            User32.GetCursorPos(out lastWheelCursorPos);
            SetCursorPos(lastWheelCursorPos);

            int delta = e.Delta;
            User32.mouse_event(MouseAction.MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero);

            StartMouseScrollTimer();
            StartAliveTimer(0);

            if (!tiny)
                ResetCursorPos(true);
        }

        private void FormMouseLeave(object sender, EventArgs e)
        {
            if (tiny)
                StartAliveTimer(1);
        }

        private bool IsBrowserWindow(IntPtr hwnd)
        {
            return PersistentWindowProcessor.IsBrowserWindow(hwnd);
        }

        void FgSleep(int ms = 200)
        {
            Thread.Sleep(ms);
        }

        void FormKeyUp(object sender, KeyEventArgs e)
        {
            try
            {
                FormKeyUpCore(sender, e);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        void FormKeyUpCore(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            bool return_focus_to_hotkey_window = true;
            //allow all ctrl alt shift modifiers
            /*
            if (e.Control || e.Alt)
                return;
            */
            if (e.KeyCode == (Keys)hotkey && e.Alt && !e.Control) 
            {
                //hotkey
                return;
            }

            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            FgSleep();

            if (e.KeyCode == Keys.Menu && !e.Control)
            {
                //forward to browser
                SendKeys.Send("%");
                return_focus_to_hotkey_window = false;
                Visible = false;
                if (!tiny)
                    StartAliveTimer(13);
            }
            else if (e.KeyCode == Keys.W)
            {
                //kill tab, ctrl + w
                SendKeys.Send("^w");
            }
            else if (e.KeyCode == Keys.T)
            {
                //new tab, ctrl + t
                if (e.Shift)
                    SendKeys.Send("^+t"); //open last closed tab
                else
                {
                    SendKeys.Send("^t"); //new tab
                    SendKeys.Send("^l"); //focus in address bar
                    return_focus_to_hotkey_window = false;
                    Visible = false;
                    defocused = true;
                    if (!tiny)
                        StartAliveTimer(2);
                }
            }
            else if (e.KeyCode == Keys.U)
            {
                //Undo close tab
                SendKeys.Send("^+t"); //open last closed tab
            }
            else if (e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F12)
            {
                //forward Function key
                int fn = e.KeyCode - Keys.F1 + 1;
                string mod = "";
                if (e.Control)
                    mod += "^";
                if (e.Alt)
                    mod += "%";
                if (e.Shift)
                    mod += "+";
                SendKeys.Send(mod + "{F" + fn + "}");
                return_focus_to_hotkey_window = false;
                Visible = false;
                if (!tiny)
                    StartAliveTimer(2);
            }
            else if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)
            {
                //forward digital key
                int digit = e.KeyCode - Keys.D0;
                string mod = "";
                mod += "^"; //force ctrl
                if (e.Alt)
                    mod += "%";
                if (e.Shift)
                    mod += "+";
                SendKeys.Send(mod + "{" + digit + "}");
            }
            /*
            else
            {
                return_focus_to_hotkey_window = false;
            }
            */

            //only allow shift
            else if (e.Control || e.Alt)
            {
                ;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                SendKeys.Send("{ESC}");
            }
            else if (e.KeyCode == Keys.Oemtilde)
            {
                //switch background color
                if (BackColor == dfltBackColor)
                    BackColor = Color.White;
                else
                    BackColor = dfltBackColor;
            }
            else if (e.KeyCode == Keys.Tab)
            {
                if (e.Shift)
                    SendKeys.Send("^+{TAB}");
                else
                    SendKeys.SendWait("^{TAB}");
            }
            else if (e.KeyCode == Keys.Q)
            {
                //prev Tab
                SendKeys.Send("^+{TAB}");
            }
            else if (e.KeyCode == Keys.E)
            {
                SendKeys.Send("{HOME}");
            }
            else if (e.KeyCode == Keys.R)
            {
                //reload
                SendKeys.Send("{F5}");
            }
            else if (e.KeyCode == Keys.A)
            {
                //address, ctrl L
                SendKeys.Send("^l");
                return_focus_to_hotkey_window = false;
                Visible = false;
                defocused = true;
                if (tiny)
                    clickThrough = true;
                else
                    StartAliveTimer(3);
            }
            else if (e.KeyCode == Keys.S)
            {
                // search
                if (e.Shift)
                    SendKeys.Send("^k");
                else
                    SendKeys.Send("^f");
                return_focus_to_hotkey_window = false;
                Visible = false;
                defocused = true;
                if (!tiny)
                    StartAliveTimer(4);
            }
            else if (e.KeyCode == Keys.D)
            {
                SendKeys.Send("{END}");
            }
            else if (e.KeyCode == Keys.F)
            {
                //next url
                SendKeys.Send("%{RIGHT}");
            }
            else if (e.KeyCode == Keys.G)
            {
                //goto tab
                //ctrl shift A (only for chrome)
                SendKeys.Send("^+a");
                Visible = false;
                defocused = true;
                if (!tiny)
                {
                    return_focus_to_hotkey_window = false;
                    StartAliveTimer(5);
                }
            }
            else if (e.KeyCode == Keys.Z)
            {
                //toggle zoom (tiny) mode
                ToggleWindowSize();
            }
            else if (e.KeyCode == Keys.X || e.KeyCode == Keys.Divide)
            {
                // goto search box
                SendKeys.Send("{DIVIDE}");
                return_focus_to_hotkey_window = false;
                Visible = false;
                defocused = true;
                if (tiny)
                    clickThrough = true;
                else
                    StartAliveTimer(4);
            }
            else if (e.KeyCode == Keys.C)
            {
                //copy (duplicate) tab
                SendKeys.Send("^l");
                SendKeys.Send("%{ENTER}");
            }
            else if (e.KeyCode == Keys.V)
            {
                //switch to last tab (chrome only)
                SendKeys.Send("^+a");
                FgSleep(300);
                SendKeys.Send("{ENTER}");
                return_focus_to_hotkey_window = false;
                if (tiny)
                    Visible = false;
                StartAliveTimer(16);
            }
            else if (e.KeyCode == Keys.B)
            {
                //backward, prev url
                SendKeys.Send("%{LEFT}");
            }
            else if (e.KeyCode == Keys.J || e.KeyCode == Keys.Down)
            {
                //down one line
                SendKeys.Send("{DOWN}");
            }
            else if (e.KeyCode == Keys.K || e.KeyCode == Keys.Up)
            {
                //up one line
                SendKeys.Send("{UP}");
            }
            else if (e.KeyCode == Keys.P)
            {
                //up one page
                SendKeys.Send("{PGUP}");
            }
            else if (e.KeyCode == Keys.Space)
            {
                //down one page
                SendKeys.Send("{PGDN}");
            }
            else if (e.KeyCode == Keys.N)
            {
                //new window
                SendKeys.Send("^n");
            }
            else if (e.KeyCode == Keys.H || e.KeyCode == Keys.Left)
            {
                //left
                SendKeys.Send("{LEFT}");
            }
            else if (e.KeyCode == Keys.L || e.KeyCode == Keys.Right)
            {
                //right
                SendKeys.Send("{RIGHT}");
            }
            else if (e.KeyCode == Keys.Delete)
            {
                //delete
                SendKeys.Send("{DEL}");
            }
            else if (e.KeyCode == Keys.Home)
            {
                SendKeys.Send("{HOME}");
            }
            else if (e.KeyCode == Keys.End)
            {
                SendKeys.Send("{END}");
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                SendKeys.Send("{PGUP}");
            }
            else if (e.KeyCode == Keys.PageDown)
            {
                SendKeys.Send("{PGDN}");
            }
            else
            {
                //User32.SetForegroundWindow(Handle); //forward to KeyUp handler
                return_focus_to_hotkey_window = false;
            }

            if (return_focus_to_hotkey_window)
            {
                User32.SetForegroundWindow(Handle);
                if (tiny)
                    ResetCursorPos();
            }
        }

        public void HotKeyPressed(bool from_menu)
        {
            if (InvokeRequired)
                BeginInvoke((Action) delegate ()
                {
                    HotKeyPressed(from_menu);
                });
            else
            {
                if (!from_menu)
                {
                    IntPtr fgwnd = GetForegroundWindow(strict : true);
                    if (fgwnd == commanderWnd)
                        fgwnd = GetForegroundWindow();
                    if (IsBrowserWindow(fgwnd))
                    {
                        invokedFromBrowser = true;
                    }
                    else
                    {
                        invokedFromBrowser = false;
                        //forward hotkey
                        char c = Convert.ToChar(hotkey);
                        string cmd = $"%{c}";
                        SendKeys.Send(cmd);
                        return;
                    }

                    //start as tiny
                    ToggleWindowSize();
                }

                if (!active)
                {
                    if (init)
                    {
                        ResetHotkeyWindowPos(from_menu);
                        init = false;
                    }
                    else
                        ResetHotKeyVirtualDesktop();

                    if (tiny)
                        ResetHotkeyWindowPos();

                    User32.SetForegroundWindow(Handle);
                    ResetCursorPos();
                    Visible = true;
                    active = true;
                }
                else
                {
                    Visible = false;
                    active = false;
                }
            }
        }


        public static void BrowserActivate(IntPtr hwnd, bool is_browser_window = true, bool in_restore = false)
        {
            if (browserWindowActivated == is_browser_window)
                return;

            browserWindowActivated = is_browser_window;
            restoring = in_restore;

            Console.WriteLine($"browser activated {hwnd.ToString("X")}");

            if (!tiny && !User32.IsWindowVisible(commanderWnd))
                return;

            StartAliveTimer(6);
        }

        private static void StartAliveTimer(int caller_id, int milliseconds = 200)
        {
            if (aliveTimer != null)
            {
                callerAliveTimer = caller_id;

                User32.GetCursorPos(out lastCursorPos);
                aliveTimer.Interval = milliseconds;
                aliveTimer.AutoReset = false;
                aliveTimer.Enabled = true;
            }
        }

        private static void StopAliveTimer()
        {
            aliveTimer.Enabled = false;
        }

        private void StartMouseScrollTimer(int milliseconds = 250)
        {
            mouseScrollDelayTimer.Interval = milliseconds;
            mouseScrollDelayTimer.AutoReset = false;
            mouseScrollDelayTimer.Enabled = true;
        }

        private void MouseScrollCallBack(Object source, ElapsedEventArgs e)
        {
            if (InvokeRequired)
                BeginInvoke((Action)delegate ()
                {
                    MouseScrollCallBack(source, e);
                });
            else if (!active)
            {
                ;
            }
            else if (Visible)
            {
                User32.SetForegroundWindow(Handle);
                //ResetCursorPos(true);
            }    
            else if (tiny)
            {
                //Visible = true; keep hiding hotkey window, let OS update cursor shape, and alive timer callback show correct hotkey window position
                User32.SetForegroundWindow(Handle);
                ResetCursorPos();
            }
            else
            {
                Visible = true;
                //ResetCursorPos(true);
            }
        }

        private static IntPtr GetCursor()
        {
            User32.CURSORINFO cursor_info;
            cursor_info.cbSize = Marshal.SizeOf(typeof(User32.CURSORINFO));
            User32.GetCursorInfo(out cursor_info);
            return cursor_info.hCursor;
        }

        private int DiffColor(Color c1, Color c2)
        {
            int result = 0;
            result += Math.Abs(c1.A - c2.A);
            result += Math.Abs(c1.R - c2.R);
            result += Math.Abs(c1.G - c2.G);
            result += Math.Abs(c1.B - c2.B);
            return result;
        }

        private bool IsSimilarColor(IntPtr hwnd, int x, int y, int xsize, int ysize, Color px)
        {
            using (Bitmap screenPixel = new Bitmap(xsize, ysize))
            {
                using (Graphics gdest = Graphics.FromImage(screenPixel))
                {
                    using (Graphics gsrc = Graphics.FromHwnd(hwnd))
                    {
                        IntPtr hsrcdc = gsrc.GetHdc();
                        IntPtr hdc = gdest.GetHdc();
                        Gdi32.BitBlt(hdc, 0, 0, xsize, ysize, hsrcdc, x, y, (int)CopyPixelOperation.SourceCopy);
                        gdest.ReleaseHdc();
                        gsrc.ReleaseHdc();
                    }
                }

                Console.WriteLine($"pixel ({x}, {y}) {px}");
                for (int i = 0; i < xsize; ++i)
                {
                    Color p = screenPixel.GetPixel(i, i);
                    if (DiffColor(p, px) > 15)
                        return false;

                    p = screenPixel.GetPixel(xsize - i - 1, i);
                    if (DiffColor(p, px) > 15)
                        return false;
                }

                return true;
            }
        }

        private bool IsUniColor(IntPtr hwnd, int x, int y, int xsize, int ysize)
        {
            using (Bitmap screenPixel = new Bitmap(xsize, ysize))
            {
                using (Graphics gdest = Graphics.FromImage(screenPixel))
                {
                    using (Graphics gsrc = Graphics.FromHwnd(hwnd))
                    {
                        IntPtr hsrcdc = gsrc.GetHdc();
                        IntPtr hdc = gdest.GetHdc();
                        Gdi32.BitBlt(hdc, 0, 0, xsize, ysize, hsrcdc, x, y, (int)CopyPixelOperation.SourceCopy);
                        gdest.ReleaseHdc();
                        gsrc.ReleaseHdc();
                    }
                }

                var px = screenPixel.GetPixel(xsize/2, 0);
                Console.WriteLine($"pixel ({x}, {y}) {px}");
                for (int i = 0; i < xsize; ++i)
                {
                    var p = screenPixel.GetPixel(i, i);
                    if (p != px)
                        return false;
                    p = screenPixel.GetPixel(xsize - i - 1, i);
                    if (p != px)
                        return false;
                }

                return true;
            }
        }

        private bool IsSameColor(IntPtr hwnd, int x, int y, int xsize, int ysize, Color px)
        {
            using (Bitmap screenPixel = new Bitmap(xsize, ysize))
            {
                using (Graphics gdest = Graphics.FromImage(screenPixel))
                {
                    using (Graphics gsrc = Graphics.FromHwnd(hwnd))
                    {
                        IntPtr hsrcdc = gsrc.GetHdc();
                        IntPtr hdc = gdest.GetHdc();
                        Gdi32.BitBlt(hdc, 0, 0, xsize, ysize, hsrcdc, x, y, (int)CopyPixelOperation.SourceCopy);
                        gdest.ReleaseHdc();
                        gsrc.ReleaseHdc();
                    }
                }

                Console.WriteLine($"pixel ({x}, {y}) {px}");
                for (int i = 0; i < xsize; ++i)
                {
                    var p = screenPixel.GetPixel(i, i);
                    if (p.ToArgb() != px.ToArgb())
                        return false;
                    p = screenPixel.GetPixel(xsize - i - 1, i);
                    if (p.ToArgb() != px.ToArgb())
                        return false;
                }

                return true;
            }
        }

        private void AliveTimerCallBack(Object source, ElapsedEventArgs e)
        {
            if (!active)
                return;

            if (tiny)
            {
                IntPtr fgwnd = GetForegroundWindow();
                if (!PersistentWindowProcessor.IsBrowserWindow(fgwnd))
                {
                    if (browserWindowActivated || restoring)
                        StartAliveTimer(6, 1000);
                    else
                        Visible = false;
                    return;
                }
                else if (restoring)
                {
                    StartAliveTimer(6, 1000);
                    return;
                }

                RECT rect = new RECT();
                User32.GetWindowRect(fgwnd, ref rect);

                POINT cursorPos;
                User32.GetCursorPos(out cursorPos);
                IntPtr cursorWnd = User32.WindowFromPoint(cursorPos);
                bool commanderWndUnderCursor = cursorWnd == Handle;

                if (!commanderWndUnderCursor && cursorWnd != fgwnd && fgwnd != User32.GetAncestor(cursorWnd, User32.GetAncestorRoot))
                {
                    //yield focus
                    //User32.SetForegroundWindow(fgwnd);
                    Visible = false;
                } 
                else if (cursorPos.Y - rect.Top <= titleHeight * 2)
                {
                    //avoid conflict with title bar
                    Visible = false;
                }
                else if (Math.Abs(cursorPos.X - lastCursorPos.X) > 3 || Math.Abs(cursorPos.Y - lastCursorPos.Y) > 3)
                {
                    ibeamCursor = false;
                    //mouse moving, continue monitor
                    totalWaitSecondsForWhiteColor = 0;
                }
                else
                {
                    IntPtr hCursor = GetCursor();
                    if (hCursor == Cursors.Default.Handle)
                    {
                        handCursor = false;

                        if (callerAliveTimer == 6)
                        {
                            //browser activation
                            ;
                        }
                        else if (!commanderWndUnderCursor && !IsUniColor(IntPtr.Zero, cursorPos.X - Width / 2, cursorPos.Y - Height / 2, 12, 12))
                        {
                            // shift commander window to allow click possible link
                            Left = cursorPos.X - 10;
                            Top = cursorPos.Y - 10;
                            totalWaitSecondsForWhiteColor = 0;
                            StartAliveTimer(11, 1000);
                            return;
                        }
                        else if (!commanderWndUnderCursor && !IsSimilarColor(IntPtr.Zero, cursorPos.X - Width / 2, cursorPos.Y - Height / 2, 1, 1, Color.White))
                        {
                            // wait for possible menu selection within webpage
                            ++totalWaitSecondsForWhiteColor;
                            if (Visible && totalWaitSecondsForWhiteColor < 3)
                            {
                                StartAliveTimer(11, 1000);
                                return;
                            }
                        }
                    }
                    else if (hCursor == Cursors.IBeam.Handle)
                    {
                        ibeamCursor = true;
                        Visible = false;
                        StartAliveTimer(11, 1000);
                        return;
                    }
                    else if (hCursor == Cursors.Cross.Handle || handCursor)
                    {
                        ibeamCursor = false;
                        StartAliveTimer(7);
                        return;
                    }

                    totalWaitSecondsForWhiteColor = 0;

                    bool regain_focus = true;
                    bool change_to_visible = false;
                    if (!Visible)
                    {
                        change_to_visible = true;
                        TopMost = true;
                        if (defocused)
                        {
                            defocused = false;
                            regain_focus = false;
                        }
                    }
                    else if (hCursor == Cursors.Default.Handle)
                    {
                        /*
                        if (!commanderWndUnderCursor)
                        {
                            User32.SetForegroundWindow(Handle);
                            User32.SetFocus(Handle);
                        }
                        */
                        regain_focus = !commanderWndUnderCursor;
                    }
                    else if (!handCursor)
                    {
                        regain_focus = false;
                    }

                    if (ibeamCursor && hCursor == Cursors.Default.Handle)
                        //&& Math.Abs(cursorPos.X - lastCursorPos.X) < 3 && Math.Abs(cursorPos.Y - lastCursorPos.Y) < 3)
                    {
                        ibeamCursor = false;
                        StartAliveTimer(11, 1000);
                        return;
                    }

                    // let tiny hotkey window follow cursor position
                    ResetHotKeyVirtualDesktop();
                    ResetHotkeyWindowPos();
                    if (change_to_visible)
                        Visible = true;
                    if (regain_focus)
                    {
                        User32.SetForegroundWindow(Handle);
                        User32.SetFocus(Handle);
                    }

                    if (hCursor == Cursors.Default.Handle)
                    {
                        //arrow cursor
                        if (!commanderWndUnderCursor)
                            StartAliveTimer(15);
                        return;
                    }

                    // hand cursor shape
                    if (!handCursor)
                    {
                        ibeamCursor = false;
                        handCursor = true;
                        Left -= 10;
                    }
                }

                StartAliveTimer(8);
            }
            else
            {
                ResetHotKeyVirtualDesktop();

                if (browserWindowActivated)
                    TopMost = true;
                else
                {
                    TopMost = false;
                    //todo, sink to bottom of z-order
                }

                POINT cursorPos;
                User32.GetCursorPos(out cursorPos);
                if (Math.Abs(cursorPos.X - lastCursorPos.X) > 3 || Math.Abs(cursorPos.Y - lastCursorPos.Y) > 3)
                {
                    StartAliveTimer(10, 1000);
                    return;
                }

                bool holding_left_button= (User32.GetKeyState(0x01) & 0x8000) != 0;
                if (holding_left_button)
                {
                    Activate();
                    return;
                }

                IntPtr hCursor = GetCursor();
                if (hCursor == Cursors.IBeam.Handle)
                {
                    StartAliveTimer(12);
                    return;
                }

                IntPtr fgwnd = GetForegroundWindow();
                if (!PersistentWindowProcessor.IsBrowserWindow(fgwnd))
                    return;

                RECT rect = new RECT();
                User32.GetWindowRect(fgwnd, ref rect);

                IntPtr cursorWnd = User32.WindowFromPoint(cursorPos);
                if (cursorWnd != Handle && cursorWnd != fgwnd && fgwnd != User32.GetAncestor(cursorWnd, User32.GetAncestorRoot))
                {
                    RECT cursorRect = new RECT();
                    User32.GetWindowRect(cursorWnd, ref cursorRect);

                    RECT intersect = new RECT();
                    User32.IntersectRect(out intersect, ref cursorRect, ref rect);

                    if (intersect.Equals(cursorRect))
                    {
                        //yield focus to internal window (right mouse invoked menu)
                        StartAliveTimer(9);
                        return;
                    }
                } 

                if (Visible)
                {
                    Console.WriteLine("webpage commander window activated by caller {0}", callerAliveTimer);
                    Activate();
                }
                else
                {
                    Visible = true;
                    TopMost = true;
                }
            }
        }

        private void buttonPrevTab_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("^+{TAB}");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonNextTab_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("^{TAB}");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonPrevUrl_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("%{LEFT}");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonNextUrl_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("%{RIGHT}");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonCloseTab_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("^w");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonNewTab_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            bool shift_key_pressed = (User32.GetKeyState(0x10) & 0x8000) != 0;
            if (shift_key_pressed)
            {
                SendKeys.Send("^T");
                User32.SetForegroundWindow(Handle);
            }
            else
            {
                SendKeys.Send("^t");
                SendKeys.Send("^l");
            }
        }

        private void buttonHome_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("{HOME}");
            User32.SetForegroundWindow(Handle);
        }

        private void buttonEnd_Click(object sender, EventArgs e)
        {
            IntPtr fgwnd = GetForegroundWindow();
            User32.SetForegroundWindow(fgwnd);
            SendKeys.Send("{END}");
            User32.SetForegroundWindow(Handle);
        }

        public static IntPtr GetForegroundWindow(bool strict = false)
        {
            return PersistentWindowProcessor.GetForegroundWindow(strict);
        }

        private void FormSizeChanged(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (promptZkey)
                {
                    MessageBox.Show("You may also press Z key to toggle the size of webpage commander window",
                        Application.ProductName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly
                    );
                    promptZkey = false;
                }

                //User32.ShowWindow(handle, (int)ShowWindowCommands.Normal);
                WindowState = FormWindowState.Normal;
                ToggleWindowSize();
                Visible = true;
            }
        }
    }
}

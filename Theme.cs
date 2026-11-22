using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

internal enum Tone { Ink, Muted, Accent, Good, Warn, Bad }

// Design tokens shared by the custom controls. Colors follow the Windows app light/dark setting.
internal static class Theme
{
    public static bool Dark;
    public static Color Window, Surface, SurfaceHi, Border, Ink, Muted, Accent, AccentSoft, OnAccent, Good, Warn, Bad, Track;
    public static event EventHandler Changed;
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

    static Theme()
    {
        Apply();
        try { SystemEvents.UserPreferenceChanged += delegate { bool was = Dark; Apply(); EventHandler h = Changed; if (was != Dark && h != null) h(null, EventArgs.Empty); }; }
        catch (Exception) { }
    }
    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    static void Apply()
    {
        bool dark = false;
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                dark = key != null && Object.Equals(key.GetValue("AppsUseLightTheme"), 0);
        } catch (Exception) { }
        Palette(dark);
    }
    internal static void Palette(bool dark)
    {
        Dark = dark;
        if (dark) {
            Window = C(21,26,29); Surface = C(32,38,42); SurfaceHi = C(44,51,56); Border = C(58,67,73);
            Ink = C(236,242,245); Muted = C(150,165,174); Accent = C(48,200,182); AccentSoft = C(22,62,60); OnAccent = C(5,32,30);
            Good = C(70,200,120); Warn = C(240,180,50); Bad = C(240,104,104); Track = C(66,76,82);
        } else {
            Window = C(243,246,247); Surface = C(255,255,255); SurfaceHi = C(237,242,243); Border = C(220,228,230);
            Ink = C(24,41,51); Muted = C(94,114,125); Accent = C(0,110,102); AccentSoft = C(221,243,240); OnAccent = C(255,255,255);
            Good = C(30,158,88); Warn = C(222,152,12); Bad = C(212,62,62); Track = C(211,222,225);
        }
    }

    static Font Cached(string name, float size, FontStyle style)
    {
        string key = name + "|" + size + "|" + (int)style;
        lock (fonts) {
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(name, size, style, GraphicsUnit.Point); fonts[key] = f; }
            return f;
        }
    }
    public static Font Face(float size, FontStyle style = FontStyle.Regular) { return Cached("Microsoft YaHei UI", size, style); }
    public static Font Icons(float size) { return Cached("Segoe MDL2 Assets", size, FontStyle.Regular); }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetDpiForSystem();
    static float scale;
    // The manifest makes the process system DPI aware, so a single scale applies to every window.
    static float Scale()
    {
        if (scale > 0f) return scale;
        float dpi = 96f;
        try { dpi = GetDpiForSystem(); }
        catch (EntryPointNotFoundException) { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; }
        scale = Math.Max(1f, dpi / 96f);
        return scale;
    }
    public static float K(Control c) { return Scale(); }
    public static int Px(Control c, float v) { return (int)Math.Round(v * K(c)); }
    public static Color Pick(Tone t)
    {
        switch (t) {
            case Tone.Muted: return Muted;
            case Tone.Accent: return Accent;
            case Tone.Good: return Good;
            case Tone.Warn: return Warn;
            case Tone.Bad: return Bad;
            default: return Ink;
        }
    }
    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
    public static Color Meter(float v) { return v < 0.72f ? Good : (v < 0.9f ? Warn : Bad); }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string app, string list);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
    // Asks Windows to draw a control's native parts (scroll bars, list headers) in the current light or dark look.
    public static void Native(Control c, bool header = false)
    {
        if (c == null || !c.IsHandleCreated) return;
        try {
            SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
            if (header) {
                IntPtr h = SendMessage(c.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
                if (h != IntPtr.Zero) SetWindowTheme(h, Dark ? "DarkMode_ItemsView" : "Explorer", null);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    public static void Quality(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half; }
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }
    public static void Outline(Graphics g, RectangleF r, float radius, Color c, float width)
    {
        using (var p = Round(r, radius)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
    }
    public static void Ring(Graphics g, RectangleF r, float radius, float k)
    {
        r.Inflate(-k, -k);
        Outline(g, r, radius, Accent, 2f * k);
    }
    public static void Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags, bool middle = true)
    {
        TextRenderer.DrawText(g, s ?? "", f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (middle ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top));
    }
    public static int MeasureWrapped(string s, Font f, int width)
    {
        return TextRenderer.MeasureText(s ?? "", f, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height;
    }
    public static int Measure(string s, Font f)
    {
        return TextRenderer.MeasureText(s ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    }
}

param([string]$step)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class Mouse {
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Find(string title) {
    foreach (var p in System.Diagnostics.Process.GetProcesses()) {
      if (p.MainWindowHandle != IntPtr.Zero) {
        var sb = new StringBuilder(256); GetWindowText(p.MainWindowHandle, sb, 256);
        if (sb.ToString().Trim() == title) return p.MainWindowHandle;
      }
    }
    return IntPtr.Zero;
  }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(120); }
  public static void FocusApp(string titlePart) {
    IntPtr target = IntPtr.Zero;
    foreach (var p in System.Diagnostics.Process.GetProcesses()) {
      if (p.MainWindowHandle != IntPtr.Zero) {
        var sb = new StringBuilder(256); GetWindowText(p.MainWindowHandle, sb, 256);
        if (sb.ToString().Trim() == titlePart) { target = p.MainWindowHandle; break; }
      }
    }
    if (target != IntPtr.Zero) {
      ShowWindow(target, 9);
      keybd_event(0x12, 0, 0, UIntPtr.Zero);
      SetForegroundWindow(target);
      keybd_event(0x12, 0, 2, UIntPtr.Zero);
      System.Threading.Thread.Sleep(400);
    }
  }
}
"@
$root = "C:\Users\coulb\Desktop\gestion-boutique-fashion"
function Shot([string]$name) {
  $h = [Mouse]::Find("Boutique")
  if ($h -ne [IntPtr]::Zero) {
    $r = New-Object Mouse+RECT
    [Mouse]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Mouse]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $bmp.Save("$root\$name")
    $g.Dispose(); $bmp.Dispose()
  } else {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $b.Size)
    $bmp.Save("$root\$name")
    $g.Dispose(); $bmp.Dispose()
  }
}
switch ($step) {
  "sale" { [Mouse]::FocusApp("Boutique"); [Mouse]::Click(117, 197); Start-Sleep -Seconds 2; Shot "shot-sale.png" }
  "keypad" { [Mouse]::FocusApp("Boutique"); [Mouse]::Click(117, 197); Start-Sleep -Seconds 2; [Mouse]::Click(600, 400); Start-Sleep -Milliseconds 400; [Mouse]::Click(1000, 577); Start-Sleep -Seconds 1; Shot "shot-keypad.png" }
  "keyboard" { [Mouse]::FocusApp("Boutique"); [Mouse]::Click(117, 563); Start-Sleep -Seconds 2; [Mouse]::Click(450, 245); Start-Sleep -Seconds 1; Shot "shot-keyboard.png" }
  "catalog" { [Mouse]::FocusApp("Boutique"); [Mouse]::Click(117, 261); Start-Sleep -Seconds 2; Shot "shot-catalog.png" }
  "close" { Get-Process BoutiqueFashion -ErrorAction SilentlyContinue | Stop-Process -Force }
}

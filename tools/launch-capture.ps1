param(
    [string]$Dll = "F:\DSH-Workspace\PackDependencyTool\src\PackDepTool.App\bin\Debug\net9.0\PackDependencyTool.dll",
    [string]$Exe = "",
    [string]$Pack = "",
    [string]$Out = "F:\DSH-Workspace\PackDependencyTool\screenshots\app.png",
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [int]$Click2X = -1,
    [int]$Click2Y = -1,
    [string]$Keys = "",
    [string]$Text = "",
    [switch]$RestoreShell,
    [switch]$MinimizeGame,
    [string]$AppArgs = ""
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class CapApp {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
  public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public static IntPtr[] FindAllByClass(string clsPrefix) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, l) => {
      var c = new StringBuilder(256); GetClassName(h, c, 256);
      if (IsWindowVisible(h) && c.ToString().StartsWith(clsPrefix)) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }
  public static IntPtr Find(uint pid, string clsPrefix, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid) {
        var c = new StringBuilder(256); GetClassName(h, c, 256);
        var t = new StringBuilder(256); GetWindowText(h, t, 256);
        if (c.ToString().StartsWith(clsPrefix) && t.ToString() == title) { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

[void][CapApp]::SetProcessDPIAware()
$dotnet = "F:\DSH-Workspace\.dotnet\dotnet.exe"
$env:DOTNET_ROOT = "F:\DSH-Workspace\.dotnet"
if ($Exe -ne "") {
    $argList = @("`"$Pack`"")
    if ($AppArgs -ne "") { $argList += $AppArgs.Split(' ') }
    $proc = Start-Process -FilePath $Exe -ArgumentList $argList -WorkingDirectory "F:\DSH-Workspace\PackDependencyTool" -PassThru
} else {
    $argList = @('exec', "`"$Dll`"", "`"$Pack`"")
    if ($AppArgs -ne "") { $argList += $AppArgs.Split(' ') }
    $proc = Start-Process -FilePath $dotnet -ArgumentList $argList -WorkingDirectory "F:\DSH-Workspace\PackDependencyTool" -PassThru
}
Start-Sleep -Seconds 8

$h = [CapApp]::Find([uint32]$proc.Id, "Avalonia-", "Pack Dependency Tool")
if ($h -eq [IntPtr]::Zero) { throw "app window not found (pid $($proc.Id))" }

# Hide the harness window so it cannot overlap the capture.
$shellHandles = @()
if ($RestoreShell) {
    Get-Process -Name "DeepSeek Harness" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.MainWindowHandle -ne 0) {
            $shellHandles += $_.MainWindowHandle
            [void][CapApp]::ShowWindow($_.MainWindowHandle, 6)
        }
    }
}

$gameHandles = @()
if ($MinimizeGame) {
    $gameHandles = [CapApp]::FindAllByClass("GLFW30")
    foreach ($gh in $gameHandles) { [void][CapApp]::ShowWindow($gh, 6) }
    Start-Sleep -Milliseconds 600
}

# Keep the app above fullscreen/always-on-top windows while capturing.
[void][CapApp]::ShowWindow($h, 9)
[void][CapApp]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
[void][CapApp]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1200

$r = New-Object CapApp+RECT
[void][CapApp]::GetWindowRect($h, [ref]$r)

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [void][CapApp]::SetCursorPos($r.Left + $ClickX, $r.Top + $ClickY)
    Start-Sleep -Milliseconds 200
    [CapApp]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [CapApp]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}

if ($Text -ne "") {
    foreach ($ch in $Text.ToCharArray()) {
        $code = [int][char]::ToUpperInvariant($ch)
        if ($code -ge 48 -and $code -le 90) {
            [CapApp]::keybd_event([byte]$code, 0, 0, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 40
            [CapApp]::keybd_event([byte]$code, 0, 2, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 90
        }
    }
    Start-Sleep -Milliseconds 300
}

if ($Click2X -ge 0 -and $Click2Y -ge 0) {
    [void][CapApp]::SetCursorPos($r.Left + $Click2X, $r.Top + $Click2Y)
    Start-Sleep -Milliseconds 250
    [CapApp]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [CapApp]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
}

if ($Keys -ne "") {
    foreach ($key in $Keys.Split(',')) {
        $vk = [byte][int]$key.Trim()
        [CapApp]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [CapApp]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 250
    }
}

$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
[void][CapApp]::SetCursorPos(2, 2)
Start-Sleep -Milliseconds 450
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

[void][CapApp]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002)
foreach ($gh in $gameHandles) { [void][CapApp]::ShowWindow($gh, 9) }
foreach ($sh in $shellHandles) { [void][CapApp]::ShowWindow($sh, 9) }

Write-Output "pid=$($proc.Id) hwnd=$h size=${w}x${hh} saved=$Out"

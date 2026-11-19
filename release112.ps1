$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Text;
public class WC {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct C { public int Flags,Type; public string TargetName,Comment; public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist,AttributeCount; public IntPtr Attributes; public string TargetAlias,UserName; }
 [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool CredRead(string t,int ty,int f,out IntPtr p);
 [DllImport("advapi32.dll")] public static extern void CredFree(IntPtr p);
 public static string[] Read(string t){IntPtr p; if(!CredRead(t,1,0,out p))return null; try{var c=(C)Marshal.PtrToStructure(p,typeof(C));var b=new byte[c.CredentialBlobSize];Marshal.Copy(c.CredentialBlob,b,0,b.Length);return new[]{c.UserName,Encoding.Unicode.GetString(b).TrimEnd('\0')};}finally{CredFree(p);}}
}
"@
$t=([WC]::Read('git:https://github.com'))[1]
$h=@{Authorization="Bearer $t";Accept='application/vnd.github+json';'User-Agent'='pdt'}
$repo='Chashao-mao/PackDependencyTool'
$body='1.1.2: download/discover is now a main page instead of a modal; task-based navigation; bigger download cards; separate environment row; Axolotl companion launch.'
try {
  $r=Invoke-RestMethod -Method Post -Headers $h -ContentType 'application/json' -Uri "https://api.github.com/repos/$repo/releases" -Body (@{tag_name='v1.1.2';target_commitish='main';name='1.1.2';body=$body;draft=$false;prerelease=$false} | ConvertTo-Json)
  "created id=$($r.id)"
} catch {
  $r=Invoke-RestMethod -Headers $h -Uri "https://api.github.com/repos/$repo/releases/tags/v1.1.2"
  "existing id=$($r.id)"
}
$path='F:\DSH-Workspace\PackDependencyTool\dist\PackDependencyTool-win-x64\PackDependencyTool.exe'
$up=Invoke-RestMethod -Method Post -Headers $h -ContentType 'application/octet-stream' -Uri "https://uploads.github.com/repos/$repo/releases/$($r.id)/assets?name=PackDependencyTool.exe" -InFile $path
"uploaded size=$($up.size)"
"url=$($r.html_url)"

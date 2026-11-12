using System.Runtime.InteropServices;
using System.Text;

var len = 0;
var rc = GetCurrentPackageFullName(ref len, null);
string package;
if (rc == 15700) package = "none";
else { var sb = new StringBuilder(len); GetCurrentPackageFullName(ref len, sb); package = sb.ToString(); }
var probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FakeCliProbe", "probe.txt");
Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
File.WriteAllText(probe, "written by the stand-in tailscale.exe");
Console.WriteLine($"stand-in tailscale 1.0; args: {string.Join(' ', args)}; package: {package}");
return 0;

[DllImport("kernel32", CharSet = CharSet.Unicode)]
static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

using System;using System.IO;using System.Linq;using System.Collections.Generic;using System.Diagnostics;using System.Drawing;using System.Windows.Forms;using System.Threading.Tasks;using System.Text.RegularExpressions;using System.Reflection;using System.Security.Cryptography;using System.IO.Compression;using System.Web.Script.Serialization;using Microsoft.Win32;
class Program {
 public const string PackageHash="PACKAGE_HASH";
 public static string Settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EmbervilleLoadingInstaller","last-folder.txt");
 [STAThread] static int Main(string[] args){
  try {
   if(args.Length==3&&args[0]=="--test-action"){var result=Run(args[1],args[2]);Console.WriteLine(result.Item2);return result.Item1;}
   if(args.Length==2&&args[0]=="--detect-output"){File.WriteAllLines(args[1],Detect());return 0;}
   Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new InstallerForm(args.Length==1?args[0]:null));return 0;
  }catch(Exception e){if(args.Length>0)return 2;MessageBox.Show(e.Message,"Emberville installer",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
 }
 public static IEnumerable<string> ParseLibraries(string text){return Regex.Matches(text,"\"path\"\\s+\"([^\"]+)\"").Cast<Match>().Select(m=>m.Groups[1].Value.Replace("\\\\","\\"));}
 public static string ManifestDir(string text){var m=Regex.Match(text,"\"installdir\"\\s+\"([^\"]+)\"");return m.Success?m.Groups[1].Value:null;}
 public static List<string> Detect(){
  var candidates=new List<string>();var steam=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  Action<string> add=p=>{try{if(!String.IsNullOrWhiteSpace(p)&&File.Exists(Path.Combine(p,"Emberville.exe"))){p=Path.GetFullPath(p);if(!candidates.Contains(p,StringComparer.OrdinalIgnoreCase))candidates.Add(p);}}catch{}};
  if(File.Exists(Settings))add(File.ReadAllText(Settings).Trim());add(AppDomain.CurrentDomain.BaseDirectory);
  foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})foreach(string key in new[]{@"Software\Valve\Steam",@"Software\WOW6432Node\Valve\Steam"})try{using(var k=hive.OpenSubKey(key)){if(k!=null)foreach(string name in new[]{"SteamPath","InstallPath"}){var v=k.GetValue(name) as string;if(!String.IsNullOrWhiteSpace(v))steam.Add(v);}}}catch{}
  steam.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam"));
  foreach(var drive in DriveInfo.GetDrives())try{if(drive.IsReady&&drive.DriveType==DriveType.Fixed){steam.Add(Path.Combine(drive.RootDirectory.FullName,"SteamLibrary"));steam.Add(Path.Combine(drive.RootDirectory.FullName,"Steam"));}}catch{}
  foreach(string home in steam.ToArray())try{string file=Path.Combine(home,"steamapps","libraryfolders.vdf");if(File.Exists(file))foreach(string library in ParseLibraries(File.ReadAllText(file)))steam.Add(library);}catch{}
  foreach(string library in steam)try{string apps=Path.Combine(library,"steamapps");add(Path.Combine(apps,"common","Emberville Demo"));add(Path.Combine(apps,"common","Emberville"));if(Directory.Exists(apps))foreach(string file in Directory.GetFiles(apps,"appmanifest_*.acf")){string content=File.ReadAllText(file);if(content.IndexOf("Emberville",StringComparison.OrdinalIgnoreCase)<0)continue;string dir=ManifestDir(content);if(dir!=null)add(Path.Combine(apps,"common",dir));}}catch{}
  return candidates;
 }
 public static string Status(string game){try{string path=Path.Combine(game,".EmbervilleLoadingPatch","state.json");if(!File.Exists(path))return "unpatched";var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));return state.ContainsKey("status")?(string)state["status"]:"unknown";}catch{return "unknown";}}
 public static Tuple<int,string> Run(string action,string game){
  if(!new[]{"Check","Install","Undo","Launch"}.Contains(action))throw new ArgumentException("Unknown action");
  game=Path.GetFullPath(game);if(!File.Exists(Path.Combine(game,"Emberville.exe")))throw new Exception("Choose the folder containing Emberville.exe.");
  string work=Path.Combine(Path.GetTempPath(),"EmbervillePatch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
  try {
   using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("PatchPackage.zip")){
    using(var sha=SHA256.Create()){string hash=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");if(hash!=PackageHash)throw new Exception("Embedded package integrity check failed.");}
    input.Position=0;using(var zip=new ZipArchive(input,ZipArchiveMode.Read))foreach(var entry in zip.Entries){string target=Path.GetFullPath(Path.Combine(work,entry.FullName));if(!target.StartsWith(work+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe archive path");if(String.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(target);continue;}Directory.CreateDirectory(Path.GetDirectoryName(target));using(var source=entry.Open())using(var dest=File.Create(target))source.CopyTo(dest);}
   }
   string script=Path.Combine(work,"Patch.ps1");string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   // Encoded command preserves spaces, apostrophes and shell metacharacters in selected paths.
   string command="$ProgressPreference='SilentlyContinue'; try { & '"+script.Replace("'","''")+"' -Action '"+action+"' -GamePath '"+game.Replace("'","''")+"'"+(action=="Install"?" -CloneSaves":"")+"; exit $LASTEXITCODE } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
   var info=new ProcessStartInfo(shell,"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand "+Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
   // A host running PowerShell 7 can inject module paths incompatible with Windows PowerShell.
   info.EnvironmentVariables.Remove("PSModulePath");
   using(var process=Process.Start(info)){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();process.WaitForExit();Task.WaitAll(stdout,stderr);return Tuple.Create(process.ExitCode,stdout.Result+stderr.Result);}
  }finally{try{if(Path.GetFullPath(work).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(work).StartsWith("EmbervillePatch-")&&!Directory.GetFileSystemEntries(work,"*",SearchOption.AllDirectories).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))Directory.Delete(work,true);}catch{}}
 }
}
class InstallerForm:Form {
 ComboBox folders=new ComboBox();Button browse=new Button(),detect=new Button(),install=new Button(),undo=new Button(),launch=new Button();Label state=new Label();TextBox log=new TextBox();ProgressBar progress=new ProgressBar();bool busy;
 public InstallerForm(string initial){
  Text="Emberville Patch Only — Experimental";ClientSize=new Size(740,560);MinimumSize=new Size(700,560);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(245,247,250);
  var title=new Label{Text="Emberville Loading Patch",Font=new Font("Segoe UI",21,FontStyle.Bold),AutoSize=true,Location=new Point(24,20)};Controls.Add(title);
  Controls.Add(new Label{Text="Patch-only installer • Demo Alpha 0.20.18.239",AutoSize=true,Location=new Point(26,68)});
  Controls.Add(new Label{Text="Game folder",AutoSize=true,Location=new Point(26,105)});
  folders.SetBounds(26,130,494,29);folders.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;folders.DropDownStyle=ComboBoxStyle.DropDown;folders.TextChanged+=(s,e)=>RefreshState();Controls.Add(folders);
  browse.Text="Browse…";browse.SetBounds(532,128,86,32);browse.Anchor=AnchorStyles.Top|AnchorStyles.Right;browse.Click+=(s,e)=>{using(var picker=new FolderBrowserDialog{Description="Choose the folder containing Emberville.exe",ShowNewFolderButton=false}){if(picker.ShowDialog(this)==DialogResult.OK)folders.Text=picker.SelectedPath;}};Controls.Add(browse);
  detect.Text="Find game";detect.SetBounds(626,128,90,32);detect.Anchor=AnchorStyles.Top|AnchorStyles.Right;detect.Click+=(s,e)=>Find();Controls.Add(detect);
  state.SetBounds(26,174,690,42);state.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(state);
  install.Text="Install patch";install.SetBounds(26,224,178,45);install.BackColor=Color.FromArgb(35,102,175);install.ForeColor=Color.White;install.FlatStyle=FlatStyle.Flat;install.Click+=async(s,e)=>await Act("Install");Controls.Add(install);
  undo.Text="Undo patch";undo.SetBounds(216,224,160,45);undo.Click+=async(s,e)=>await Act("Undo");Controls.Add(undo);
  launch.Text="Launch game";launch.SetBounds(388,224,160,45);launch.Click+=async(s,e)=>await Act("Launch");Controls.Add(launch);
  Controls.Add(new Label{Text="Install rebuilds files locally (allow several minutes and 12 GB temporary space).\nUndo restores originals. Normal saves stay untouched; test saves are separate.",Location=new Point(26,285),Size=new Size(690,48),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right});
  progress.SetBounds(26,342,690,10);progress.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(progress);
  log.SetBounds(26,365,690,136);log.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.BackColor=Color.White;Controls.Add(log);
  Controls.Add(new Label{Text="Full dungeon gameplay remains unverified. Close the game before Install or Undo.",Location=new Point(26,515),Size=new Size(690,24),Anchor=AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right});
  FormClosing+=(s,e)=>{if(busy){e.Cancel=true;MessageBox.Show(this,"Please wait for the current operation to finish.","Operation in progress");}};
  Shown+=(s,e)=>{Find();if(initial!=null)folders.Text=initial;};
 }
 void Find(){var found=Program.Detect();folders.Items.Clear();foreach(var p in found)folders.Items.Add(p);if(found.Count>0)folders.SelectedIndex=0;else log.Text="No game folder found in the detected Steam libraries. Click Browse to select a separate game copy.";RefreshState();}
 void RefreshState(){bool valid=Directory.Exists(folders.Text)&&File.Exists(Path.Combine(folders.Text,"Emberville.exe"));string status=valid?Program.Status(folders.Text):"missing";state.Text=!valid?"Choose your game folder to begin.":status=="installed"?"Patch installed. You can play or restore the original game files.":status=="installing"?"Installation interrupted. Click Undo patch to restore the backup.":status=="restored"?"Original files restored. You can install again; test progress will be kept.":status=="unknown"?"The patch journal could not be read. Preserve the backup folder.":"Game folder found. Exact compatibility will be checked before installation.";install.Enabled=!busy&&valid&&(status=="unpatched"||status=="restored");undo.Enabled=!busy&&valid&&(status=="installed"||status=="installing");launch.Enabled=!busy&&valid&&status=="installed";}
 async Task Act(string action){
  string path=folders.Text;busy=true;folders.Enabled=browse.Enabled=detect.Enabled=false;RefreshState();progress.Style=ProgressBarStyle.Marquee;log.Text=action+" in progress. Checking files and preparing the bundled patch…";
  try{var result=await Task.Run(()=>Program.Run(action,path));log.Text=result.Item2;if(result.Item1==0){Directory.CreateDirectory(Path.GetDirectoryName(Program.Settings));File.WriteAllText(Program.Settings,path);}else log.AppendText("\r\nIf access was denied, use a writable game copy or run this installer as administrator.");}
  catch(Exception e){log.Text=e.Message;}
  finally{busy=false;folders.Enabled=browse.Enabled=detect.Enabled=true;progress.Style=ProgressBarStyle.Blocks;RefreshState();}
 }
}

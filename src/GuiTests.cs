using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using PhotoSieve;

static class GuiTests {
    static int passed;
    static T Field<T>(object o,string name) {return (T)o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Invoke(object o,string name,params object[] args){o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,args);}
    static void Check(bool condition,string message){if(!condition)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);passed++;}
    static void Wait(MainForm form){var sw=Stopwatch.StartNew();while(Field<bool>(form,"busy")&&sw.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(10);}Application.DoEvents();Check(!Field<bool>(form,"busy"),"background operation completes");}
    static void Snapshot(Form form,string path){Application.DoEvents();using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(path,ImageFormat.Png);}}
    [STAThread] static int Main(string[] args) {
        try {
            string photos=Path.GetFullPath(args[0]),dest=Path.GetFullPath(args[1]);Directory.CreateDirectory(dest);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=(s,e)=>Console.WriteLine("UI EXCEPTION: "+e.Exception.Message+"\n"+e.Exception.StackTrace);
            Control.CheckForIllegalCrossThreadCalls=true;
            using(var form=new MainForm()) {
                Exception failure=null;
                form.Shown+=(sender,eventArgs)=>form.BeginInvoke(new Action(()=>{
                try {
                Application.DoEvents();
                Check(Field<Surface>(form,"dropSurface").Visible && !Field<Button>(form,"remove").Enabled,"empty state guides user and disables removal");
                Snapshot(form,Path.Combine(dest,"界面-首页.png"));
                form.Size=new Size(1040,740);Application.DoEvents();
                var empty=Field<Panel>(form,"emptyState");var choose=Field<Button>(form,"emptyChoose");
                Check(choose.Visible&&choose.Bottom<=empty.Height,"folder choice remains visible at minimum size");
                Snapshot(form,Path.Combine(dest,"ui-empty-small.png"));
                form.Size=new Size(1420,930);Application.DoEvents();
                var data=new DataObject(DataFormats.FileDrop,new string[]{photos});var drag=new DragEventArgs(data,0,0,0,DragDropEffects.Copy,DragDropEffects.None);
                var drop=Field<Surface>(form,"dropSurface");Invoke(drop,"OnDragEnter",drag);Check(drag.Effect==DragDropEffects.Copy,"folder drag accepted");Invoke(drop,"OnDragDrop",drag);
                Check(Field<TextBox>(form,"folder").Text==photos,"folder drop populates scan path");
                var invalid=new DragEventArgs(new DataObject(DataFormats.FileDrop,new string[]{Path.Combine(photos,"01_原图.jpg")}),0,0,0,DragDropEffects.Copy,DragDropEffects.None);Invoke(drop,"OnDragEnter",invalid);Check(invalid.Effect==DragDropEffects.None,"individual file rejected by folder drop target");
                Field<Button>(form,"scan").PerformClick();Wait(form);
                Check(Field<ListBox>(form,"groups").Items.Count==1 && Field<Label>(form,"readValue").Text=="5","real UI scan populates groups and statistics");
                var tiles=Field<FlowLayoutPanel>(form,"tiles");
                Check(tiles.Visible&&tiles.Controls.Count==4&&!drop.Visible,"scan replaces empty state with photo cards");
                Field<Button>(form,"suggest").PerformClick();
                Check(Field<Label>(form,"selection").Text=="已勾选 3 张" && Field<Button>(form,"remove").Enabled,"group selection updates count and removal availability");
                Check(!tiles.Controls[0].Controls.OfType<CheckBox>().First().Checked,"suggested selection preserves highest resolution reference");
                Check(Field<Label>(form,"spaceValue").Text!="0.0 KB","selected file size statistic updates");
                Snapshot(form,Path.Combine(dest,"界面-筛查结果.png"));
                Field<Button>(form,"clear").PerformClick();
                Check(!Field<Button>(form,"remove").Enabled && Field<Label>(form,"selection").Text=="已勾选 0 张","clear selection resets controls");
                var timer=new System.Windows.Forms.Timer {Interval=80};bool opened=false;
                timer.Tick+=(s,e)=>{var preview=Application.OpenForms.OfType<Preview>().FirstOrDefault();if(preview==null)return;timer.Stop();Snapshot(preview,Path.Combine(dest,"界面-大图对照.png"));opened=true;preview.Close();};timer.Start();
                tiles.Controls[1].Controls.OfType<Button>().First().PerformClick();timer.Dispose();Check(opened,"comparison button opens actual side-by-side preview");
                form.Size=new Size(1040,740);Application.DoEvents();Snapshot(form,Path.Combine(dest,"ui-results-small.png"));
                var remove=Field<Button>(form,"remove");Check(form.ClientRectangle.Contains(form.PointToClient(remove.PointToScreen(Point.Empty))),"bottom actions remain in window at minimum size");
                // Check page transitions on >24 real, unique fixture files.
                var result=Field<ScanResult>(form,"result");var g=result.Groups[0];string pageDir=Path.Combine(dest,"pagination-fixtures");Directory.CreateDirectory(pageDir);
                for(int i=0;i<23;i++){string path=Path.Combine(pageDir,"copy_"+i+".jpg");File.Copy(g.Photos[0].Path,path,true);g.Photos.Add(Engine.Analyze(path));}
                form.LoadDemo(result);tiles=Field<FlowLayoutPanel>(form,"tiles");
                tiles.Controls[1].Controls.OfType<CheckBox>().First().Checked=true;Field<Button>(form,"next").PerformClick();
                Check(tiles.Controls.Count==3 && Field<Label>(form,"pageText").Text=="2 / 2","pagination renders remaining photos");
                Field<Button>(form,"prev").PerformClick();Check(tiles.Controls[1].Controls.OfType<CheckBox>().First().Checked,"selection survives pagination");
                Field<Button>(form,"scan").PerformClick();Field<Button>(form,"cancel").PerformClick();Wait(form);
                Check(Field<ScanResult>(form,"result").Cancelled && Field<Surface>(form,"dropSurface").Visible,"cancel returns to recoverable empty state");
                } catch(Exception ex) {failure=ex;} finally {form.Close();}
                }));
                Application.Run(form);
                if(failure!=null)throw failure;
            }
            Console.WriteLine("ALL "+passed+" GUI CHECKS PASSED");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}

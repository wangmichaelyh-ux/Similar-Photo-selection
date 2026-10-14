using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PhotoSieve;

static class Tests {
    static int passed;
    static void Check(bool ok,string label) { if(!ok)throw new Exception("FAIL: "+label);Console.WriteLine("PASS: "+label);passed++; }
    static void Save(Bitmap b,string p,long quality) {
        var encoder=ImageCodecInfo.GetImageEncoders().First(c=>c.MimeType=="image/jpeg");
        using(var param=new EncoderParameters(1)) {param.Param[0]=new EncoderParameter(Encoder.Quality,quality);b.Save(p,encoder,param);}
    }
    static Bitmap Scene(int variant) {
        var b=new Bitmap(1000,700);
        using(var g=Graphics.FromImage(b)) {
            g.Clear(variant==0?Color.LightSkyBlue:Color.Beige);
            g.FillRectangle(Brushes.ForestGreen,0,450,1000,250);
            if(variant==0) {
                g.FillEllipse(Brushes.Gold,710,70,160,160);g.FillPolygon(Brushes.SlateGray,new[]{new Point(0,460),new Point(360,170),new Point(650,460)});
                g.FillRectangle(Brushes.Sienna,620,350,190,240);g.FillRectangle(Brushes.LightYellow,655,405,42,80);
                g.FillEllipse(Brushes.White,140,60,190,65);g.FillEllipse(Brushes.White,290,90,200,70);
            } else {
                for(int i=0;i<10;i++)g.FillRectangle(i%2==0?Brushes.Crimson:Brushes.DarkBlue,i*100,40,70,610);
            }
            using(var f=new Font("Arial",30))g.DrawString(variant==0?"SUMMER 2026":"CITY AT NIGHT",f,Brushes.White,40,600);
        }return b;
    }
    [STAThread] static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string nested=Path.Combine(root,"子文件夹");Directory.CreateDirectory(nested);
            using(var b=Scene(0)) {
                Save(b,Path.Combine(root,"01_原图.jpg"),96);Save(b,Path.Combine(root,"03_压缩版本.jpeg"),60);
                using(var small=Engine.Resize(b,600,420))Save(small,Path.Combine(root,"04_缩小版本.JPG"),88);
            }
            File.Copy(Path.Combine(root,"01_原图.jpg"),Path.Combine(nested,"02_完全复制.JPEG"),true);
            using(var b=Scene(1))Save(b,Path.Combine(root,"05_不同画面.jpg"),92);
            File.WriteAllText(Path.Combine(root,"06_损坏.jpeg"),"not a jpeg");File.WriteAllText(Path.Combine(root,"ignored.png"),"ignored");
            var a=Engine.Analyze(Path.Combine(root,"01_原图.jpg"));var compressed=Engine.Analyze(Path.Combine(root,"03_压缩版本.jpeg"));var resized=Engine.Analyze(Path.Combine(root,"04_缩小版本.JPG"));var different=Engine.Analyze(Path.Combine(root,"05_不同画面.jpg"));
            Check(Engine.Similar(a,compressed,1),"strict detects recompressed JPEG");Check(Engine.Similar(a,resized,1),"strict detects resized JPEG");Check(!Engine.Similar(a,different,3),"reject unrelated scene even in loose mode");Check(!Engine.Similar(a,compressed,0),"exact mode excludes recompressed bytes");
            var result=Engine.Scan(root,true,1,()=>false,(v,t)=>{});
            Check(result.Found==6 && result.Read==5 && result.Errors.Count==1,"recursive scan, extension casing and corrupt image reporting");
            Check(result.Groups.Count==1 && result.Groups[0].Photos.Count==4,"four similar photos form one group");
            var exact=Engine.Scan(root,true,0,()=>false,(v,t)=>{});Check(exact.Groups.Count==1 && exact.Groups[0].Photos.Count==2 && exact.Groups[0].Exact,"SHA256 exact-only group");
            var top=Engine.Scan(root,false,0,()=>false,(v,t)=>{});Check(top.Found==5 && top.Groups.Count==0,"nonrecursive scan excludes nested copy");
            Check(Engine.Scan(root,true,1,()=>true,(v,t)=>{}).Cancelled,"cancellation");
            Check(Engine.Unchanged(a),"unchanged file validated");
            var mutated=Engine.Analyze(Path.Combine(root,"05_不同画面.jpg"));File.SetLastWriteTimeUtc(mutated.Path,mutated.Modified.AddSeconds(5));Check(!Engine.Unchanged(mutated),"changed file rejected before deletion");
            byte[] altered=File.ReadAllBytes(mutated.Path);altered[altered.Length-10]^=1;File.WriteAllBytes(mutated.Path,altered);File.SetLastWriteTimeUtc(mutated.Path,mutated.Modified);
            Check(!Engine.Unchanged(mutated),"hash rejects mutation even when timestamp and length match");
            var cancelGroup=Engine.Group(new List<Photo>{a,compressed},1,()=>true);Check(cancelGroup.Count==0,"grouping cancellation");
            string export=Engine.Export(result,Path.GetDirectoryName(root),(v,t)=>{});Check(Directory.GetFiles(export,"*.jpg",SearchOption.AllDirectories).Length+Directory.GetFiles(export,"*.jpeg",SearchOption.AllDirectories).Length==4,"export copies all four candidates");
            Check(File.Exists(Path.Combine(export,"原始路径对照.csv")) && File.ReadAllText(Path.Combine(export,"原始路径对照.csv")).Contains(a.Path),"UTF8 manifest retains original paths");
            Check(File.Exists(a.Path),"export preserves originals");
            // Exercise zero/63/64-bit Hamming boundaries and reject color/aspect collisions.
            Check(Engine.Distance(0,ulong.MaxValue)==64 && Engine.Distance(0,1UL<<63)==1,"64-bit Hamming correctness");
            var color=new Photo {Sha="different",Width=a.Width,Height=a.Height,PHash=a.PHash,DHash=a.DHash,R=a.R+100,G=a.G,B=a.B};
            Check(!Engine.Similar(a,color,3),"color guard rejects feature collision");color.R=a.R;color.Width*=2;Check(!Engine.Similar(a,color,3),"aspect guard rejects feature collision");
            string orientations=Path.Combine(Path.GetDirectoryName(root),"orientation-test");
            if(Directory.Exists(orientations)) {
                var normal=Engine.Analyze(Path.Combine(orientations,"normal.jpg"));
                for(int i=2;i<=8;i++) {
                    var turned=Engine.Analyze(Path.Combine(orientations,"orientation_"+i+".jpg"));
                    Check(turned.Width==normal.Width && turned.Height==normal.Height && Engine.Similar(normal,turned,1),"EXIF orientation "+i+" normalizes for matching");
                }
            }
            // Direct-anchor grouping should not connect A to C only through B.
            var ga=new Photo {Sha="a",Path="a",Width=100,Height=100,Bytes=3,PHash=0,DHash=0};
            var gb=new Photo {Sha="b",Path="b",Width=100,Height=100,Bytes=2,PHash=63,DHash=0};
            var gc=new Photo {Sha="c",Path="c",Width=100,Height=100,Bytes=1,PHash=4095,DHash=0};
            var chain=Engine.Group(new List<Photo>{ga,gb,gc},1,()=>false);
            Check(chain.Count==1 && chain[0].Photos.Count==2 && !chain[0].Photos.Contains(gc),"no transitive similarity chains");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new MainForm()) {
                form.LoadDemo(result);form.Show();Application.DoEvents();
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(Path.GetDirectoryName(root),"ui-check.png"));}
                form.Size=new Size(1020,720);Application.DoEvents();
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(Path.GetDirectoryName(root),"ui-small-check.png"));}
                form.Close();
            }
            Check(true,"WinForms window and thumbnail render smoke test");
            Console.WriteLine("ALL "+passed+" CHECKS PASSED");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}

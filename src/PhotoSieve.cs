using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PhotoSieve {
    public sealed class Photo {
        public string Path, Sha;
        public long Bytes;
        public DateTime Modified;
        public int Width, Height;
        public ulong PHash, DHash;
        public double R, G, B;
    }
    public sealed class PhotoGroup {
        public List<Photo> Photos = new List<Photo>();
        public bool Exact { get { return Photos.Select(p => p.Sha).Distinct().Count() == 1; } }
        public Photo Best { get { return Photos.OrderByDescending(p => (long)p.Width * p.Height).ThenByDescending(p => p.Bytes).First(); } }
    }
    public sealed class ScanResult {
        public List<PhotoGroup> Groups = new List<PhotoGroup>();
        public List<string> Errors = new List<string>();
        public int Found, Read, Mode;
        public bool Cancelled;
        public string Root;
    }
    public sealed class ScanProgress {
        public int Value;
        public string Text;
        public ScanProgress(int v, string t) { Value = v; Text = t; }
    }
    public static class Engine {
        static readonly double[,] Cos = MakeCos();
        static double[,] MakeCos() {
            var c = new double[8,32];
            for (int u=0;u<8;u++) for (int x=0;x<32;x++) c[u,x] = Math.Cos((2*x+1)*u*Math.PI/64);
            return c;
        }
        public static Bitmap ReadImage(string path) {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var im = Image.FromStream(fs, false, true)) {
                Orient(im);
                return new Bitmap(im);
            }
        }
        static void Orient(Image im) {
            try {
                if (!im.PropertyIdList.Contains(0x112)) return;
                var v=im.GetPropertyItem(0x112).Value;
                int o=v[0];
                RotateFlipType[] types = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone,
                    RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                    RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                if(o>=1 && o<=8) im.RotateFlip(types[o]);
            } catch(ArgumentException) { }
        }
        public static Bitmap Resize(Image source, int w, int h) {
            var bm=new Bitmap(w,h,PixelFormat.Format24bppRgb);
            using(var g=Graphics.FromImage(bm)) {
                g.Clear(Color.White);
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                using(var attr=new ImageAttributes()) {
                    attr.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(source,new Rectangle(0,0,w,h),0,0,source.Width,source.Height,GraphicsUnit.Pixel,attr);
                }
            }
            return bm;
        }
        public static Bitmap Thumbnail(string path, int w, int h) {
            using(var image=ReadImage(path)) {
                double r=Math.Min((double)w/image.Width,(double)h/image.Height);
                return Resize(image, Math.Max(1,(int)(image.Width*r)),Math.Max(1,(int)(image.Height*r)));
            }
        }
        public static string Digest(string path) {
            using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(s)).Replace("-","");
        }
        public static Photo Analyze(string path) {
            var info=new FileInfo(path);
            var p=new Photo {Path=path,Bytes=info.Length,Modified=info.LastWriteTimeUtc};
            // A read lock covers both byte hashing and decoding so one record describes one version.
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                using(var sha=SHA256.Create()) p.Sha=BitConverter.ToString(sha.ComputeHash(locked)).Replace("-","");
                locked.Position=0;
                using(var image=Image.FromStream(locked,false,true)) {
                    Orient(image); p.Width=image.Width; p.Height=image.Height;
                    using(var small=Resize(image,32,32)) {
                        var gray=new double[32,32];
                        for(int y=0;y<32;y++) for(int x=0;x<32;x++) {
                            var c=small.GetPixel(x,y);
                            gray[x,y]=c.R*.299+c.G*.587+c.B*.114;
                            p.R+=c.R; p.G+=c.G; p.B+=c.B;
                        }
                        p.R/=1024; p.G/=1024; p.B/=1024;
                        var temp=new double[8,32];
                        for(int u=0;u<8;u++) for(int y=0;y<32;y++) for(int x=0;x<32;x++) temp[u,y]+=gray[x,y]*Cos[u,x];
                        var ac=new List<double>();
                        for(int v=0;v<8;v++) for(int u=0;u<8;u++) {
                            if(u==0 && v==0) continue;
                            double sum=0;
                            for(int y=0;y<32;y++) sum+=temp[u,y]*Cos[v,y];
                            if(u==0) sum/=Math.Sqrt(2); if(v==0) sum/=Math.Sqrt(2);
                            ac.Add(sum);
                        }
                        double median=ac.OrderBy(a=>a).ElementAt(31);
                        for(int i=0;i<63;i++) if(ac[i]>median+1e-6) p.PHash|=1UL<<i;
                    }
                    using(var small=Resize(image,9,8)) {
                        for(int y=0;y<8;y++) for(int x=0;x<8;x++) {
                            var a=small.GetPixel(x,y); var b=small.GetPixel(x+1,y);
                            if(a.R*.299+a.G*.587+a.B*.114 > b.R*.299+b.G*.587+b.B*.114) p.DHash|=1UL<<(y*8+x);
                        }
                    }
                }
            }
            info.Refresh();
            if(info.Length!=p.Bytes || info.LastWriteTimeUtc!=p.Modified) throw new IOException("扫描期间文件发生了变化");
            return p;
        }
        public static int Distance(ulong a,ulong b) { ulong x=a^b; int n=0; while(x!=0) {x&=x-1;n++;} return n; }
        public static bool Similar(Photo a,Photo b,int mode) {
            if(a.Sha==b.Sha) return true;
            if(mode==0) return false;
            int[] pt={0,6,10,14},dt={0,8,14,20};
            double ar=(double)a.Width/a.Height,br=(double)b.Width/b.Height;
            return Math.Abs(ar/br-1)<=.05 && Distance(a.PHash,b.PHash)<=pt[mode] && Distance(a.DHash,b.DHash)<=dt[mode]
                && Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))<= (mode==3?55:35);
        }
        // BK tree indexes representatives; members must match their representative directly.
        sealed class Node {
            public PhotoGroup Group;
            public Dictionary<int,Node> Children=new Dictionary<int,Node>();
            public Node(PhotoGroup g) {Group=g;}
        }
        static void Add(ref Node root,PhotoGroup group) {
            if(root==null) {root=new Node(group);return;}
            var n=root;
            while(true) {
                int d=Distance(group.Photos[0].PHash,n.Group.Photos[0].PHash);
                Node next;
                if(!n.Children.TryGetValue(d,out next)) {n.Children.Add(d,new Node(group));return;}
                n=next;
            }
        }
        static PhotoGroup Match(Node root, Photo photo,int mode) {
            if(root==null) return null;
            var stack=new Stack<Node>();stack.Push(root);
            int radius=new int[]{0,6,10,14}[mode],best=999;
            PhotoGroup match=null;
            while(stack.Count>0) {
                var n=stack.Pop(); var anchor=n.Group.Photos[0]; int d=Distance(anchor.PHash,photo.PHash);
                if(d<best && Similar(anchor,photo,mode)) {best=d;match=n.Group;}
                foreach(var child in n.Children) if(child.Key>=d-radius && child.Key<=d+radius) stack.Push(child.Value);
            }
            return match;
        }
        public static List<PhotoGroup> Group(List<Photo> photos,int mode,Func<bool> cancel) {
            var all=new List<PhotoGroup>(); var exact=new Dictionary<string,PhotoGroup>(); Node root=null;
            foreach(var p in photos.OrderByDescending(p=>(long)p.Width*p.Height).ThenByDescending(p=>p.Bytes).ThenBy(p=>p.Path,StringComparer.OrdinalIgnoreCase)) {
                if(cancel()) break;
                PhotoGroup g;
                if(!exact.TryGetValue(p.Sha,out g)) {
                    g=mode==0?null:Match(root,p,mode);
                    if(g==null) {g=new PhotoGroup(); g.Photos.Add(p);all.Add(g); if(mode>0) Add(ref root,g);}
                    else g.Photos.Add(p);
                    exact.Add(p.Sha,g);
                } else g.Photos.Add(p);
            }
            return all.Where(g=>g.Photos.Count>1).OrderByDescending(g=>g.Exact).ThenByDescending(g=>g.Photos.Count).ToList();
        }
        public static ScanResult Scan(string folder,bool recursive,int mode,Func<bool> cancel,Action<int,string> progress) {
            var result=new ScanResult {Root=folder,Mode=mode}; var paths=new List<string>(); var pending=new Stack<string>();pending.Push(folder);
            while(pending.Count>0 && !cancel()) {
                string dir=pending.Pop();
                try {
                    foreach(var file in Directory.EnumerateFiles(dir)) {
                        if(cancel()) break;
                        var ext=System.IO.Path.GetExtension(file);
                        if((ext.Equals(".jpg",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpeg",StringComparison.OrdinalIgnoreCase)) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)==0) paths.Add(file);
                    }
                    if(recursive) foreach(var sub in Directory.EnumerateDirectories(dir)) if((File.GetAttributes(sub)&FileAttributes.ReparsePoint)==0) pending.Push(sub);
                } catch(Exception ex) {result.Errors.Add(dir+"："+ex.Message);}
                progress(0,"正在查找 JPEG · 已发现 "+paths.Count+" 张");
            }
            result.Found=paths.Count; var photos=new List<Photo>();
            for(int i=0;i<paths.Count && !cancel();i++) {
                try {photos.Add(Analyze(paths[i]));result.Read++;} catch(Exception ex) {result.Errors.Add(paths[i]+"："+ex.Message);}
                progress((i+1)*90/Math.Max(1,paths.Count),"分析 "+(i+1)+" / "+paths.Count+" · "+System.IO.Path.GetFileName(paths[i]));
            }
            if(cancel()) {result.Cancelled=true;return result;}
            progress(93,"正在对比图像特征并分组…");
            result.Groups=Group(photos,mode,cancel); result.Cancelled=cancel();
            progress(100,"扫描完成"); return result;
        }
        public static bool Unchanged(Photo p) {
            var f=new FileInfo(p.Path);
            return f.Exists && f.Length==p.Bytes && f.LastWriteTimeUtc==p.Modified && Digest(p.Path)==p.Sha;
        }
        public static string Csv(string value) {return "\""+value.Replace("\"","\"\"")+"\"";}
        public static void Report(ScanResult result,string file) {
            using(var w=new StreamWriter(file,false,new UTF8Encoding(true))) {
                w.WriteLine("组号,类型,原始路径,宽度,高度,字节数,SHA256");
                for(int i=0;i<result.Groups.Count;i++) foreach(var p in result.Groups[i].Photos)
                    w.WriteLine((i+1)+","+Csv(result.Groups[i].Exact?"文件完全相同":"画面相似，需人工确认")+","+Csv(p.Path)+","+p.Width+","+p.Height+","+p.Bytes+","+p.Sha);
            }
        }
        public static string Export(ScanResult result,string parent,Action<int,string> progress) {
            string dest=System.IO.Path.Combine(parent,"照片筛查_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+"_"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(dest); int count=0,total=result.Groups.Sum(g=>g.Photos.Count);
            for(int i=0;i<result.Groups.Count;i++) {
                string dir=System.IO.Path.Combine(dest,"组_"+(i+1).ToString("0000"));Directory.CreateDirectory(dir);
                for(int j=0;j<result.Groups[i].Photos.Count;j++) {
                    var p=result.Groups[i].Photos[j];
                    if(!Unchanged(p)) throw new IOException("照片已变化，请重新扫描："+p.Path+"\r\n已复制的文件保存在："+dest);
                    File.Copy(p.Path,System.IO.Path.Combine(dir,(j+1).ToString("000")+"_"+System.IO.Path.GetFileName(p.Path)),false);
                    progress(++count*100/Math.Max(1,total),"导出副本 "+count+" / "+total);
                }
            }
            Report(result,System.IO.Path.Combine(dest,"原始路径对照.csv")); return dest;
        }
    }
    public static class Recycle {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct FileOp {public IntPtr hwnd;public uint wFunc;[MarshalAs(UnmanagedType.LPWStr)] public string from;[MarshalAs(UnmanagedType.LPWStr)] public string to;public ushort flags;[MarshalAs(UnmanagedType.Bool)] public bool aborted;public IntPtr mapping;[MarshalAs(UnmanagedType.LPWStr)] public string title;}
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHFileOperation(ref FileOp op);
        public static void Send(string path,IntPtr owner) {
            var op=new FileOp {hwnd=owner,wFunc=3,from=path+"\0\0",flags=0x0040|0x0100};
            // Keep Windows confirmation/error UI: some volumes cannot recycle.
            int code=SHFileOperation(ref op);
            if(op.aborted) throw new OperationCanceledException("已取消回收操作");
            if(code!=0) throw new IOException("Windows 未能回收此文件，错误码 "+code);
            if(File.Exists(path)) throw new IOException("文件仍存在，未标记为已移除");
        }
    }
    public sealed class Preview : Form {
        Bitmap loaded;
        public Preview(Photo a,Photo b) {
            Text="大图对照 · 左侧为参考照片"; Width=1260;Height=800;MinimumSize=new Size(760,500);StartPosition=FormStartPosition.CenterParent;
            BackColor=Color.FromArgb(24,29,38); Font=new Font("Microsoft YaHei UI",10);
            var table=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=b==null?1:2,RowCount=1};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,b==null?100:50));if(b!=null)table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));Controls.Add(table);
            Add(table,a,0);if(b!=null) Add(table,b,1);
        }
        void Add(TableLayoutPanel table,Photo p,int column) {
            var panel=new Panel {Dock=DockStyle.Fill,Padding=new Padding(12)}; table.Controls.Add(panel,column,0);
            var label=new Label {Dock=DockStyle.Bottom,Height=76,ForeColor=Color.White,Text=p.Path+"\r\n"+p.Width+" × "+p.Height+" · "+MainForm.SizeText(p.Bytes),AutoEllipsis=true};
            var box=new PictureBox {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(16,20,27)};
            var fit=new CheckBox {Dock=DockStyle.Top,Height=34,ForeColor=Color.White,Text="原始像素（滚动查看）"};
            var scroller=new Panel {Dock=DockStyle.Fill,AutoScroll=true};scroller.Controls.Add(box);panel.Controls.Add(scroller);panel.Controls.Add(label);panel.Controls.Add(fit);
            try {loaded=Engine.ReadImage(p.Path);box.Image=loaded;}catch(Exception ex){label.Text=ex.Message;}
            fit.CheckedChanged+=(s,e)=> {box.Dock=fit.Checked?DockStyle.None:DockStyle.Fill;box.SizeMode=fit.Checked?PictureBoxSizeMode.AutoSize:PictureBoxSizeMode.Zoom;};
            box.Disposed+=(s,e)=>{if(box.Image!=null)box.Image.Dispose();};
        }
    }
    public sealed class MainForm : Form {
        readonly Color ink=Color.FromArgb(29,51,55),muted=Color.FromArgb(108,127,132),accent=Color.FromArgb(17,112,98);
        TextBox folder;CheckBox recursive;ComboBox mode;Button browse,scan,cancel,export,report,remove,suggest,clear,prev,next,issues;
        Label status,summary,detail,selection,pageText;ProgressBar progress;ListBox groups;FlowLayoutPanel tiles;
        Label readValue,groupValue,spaceValue,groupTitle,groupCount,groupPlaceholder,emptyTitle,emptyCopy;
        Panel emptyState;Button emptyChoose;Surface dropSurface;FlowLayoutPanel steps;
        BackgroundWorker worker;ScanResult result;HashSet<string> selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool busy,rendering,resizingCards;int page; const int PageSize=24;
        public MainForm() {
            Text="照片筛查 · Photo Sieve 1.1";Width=1420;Height=930;MinimumSize=new Size(1040,740);StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",9);BackColor=Color.FromArgb(243,247,246);ForeColor=ink;AutoScaleMode=AutoScaleMode.Dpi;
            DoubleBuffered=true;
            try {Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);} catch { }
            var shell=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Padding=new Padding(18),Margin=Padding.Empty};
            shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,232));shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(shell);

            var sidebar=new Surface {Dock=DockStyle.Fill,Padding=new Padding(17),Margin=new Padding(0,0,18,0)};shell.Controls.Add(sidebar,0,0);
            var side=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,BackColor=Color.White,Margin=Padding.Empty};
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute,106));side.RowStyles.Add(new RowStyle(SizeType.Absolute,72));side.RowStyles.Add(new RowStyle(SizeType.Absolute,45));side.RowStyles.Add(new RowStyle(SizeType.Percent,100));side.RowStyles.Add(new RowStyle(SizeType.Absolute,118));sidebar.Controls.Add(side);
            var brand=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            brand.Controls.Add(new Label {Text="照片筛查",Font=new Font(Font.FontFamily,22,FontStyle.Bold),AutoSize=true,Location=new Point(0,9),ForeColor=accent});
            brand.Controls.Add(new Label {Text="P H O T O   S I E V E",Font=new Font("Segoe UI",8,FontStyle.Bold),AutoSize=true,Location=new Point(3,56),ForeColor=muted});side.Controls.Add(brand,0,0);
            var local=new Surface {Dock=DockStyle.Fill,Fill=Color.FromArgb(236,246,242),Stroke=Color.FromArgb(236,246,242),Padding=new Padding(10),Margin=new Padding(0,0,0,12),Radius=9};
            local.Controls.Add(new Label {Dock=DockStyle.Fill,Text="●  本地处理\r\n照片留在你的电脑里",ForeColor=accent,TextAlign=ContentAlignment.MiddleLeft,BackColor=Color.FromArgb(236,246,242)});side.Controls.Add(local,0,1);
            groupCount=new Label {Dock=DockStyle.Fill,Text="相似分组  /  0",TextAlign=ContentAlignment.MiddleLeft,Font=new Font(Font.FontFamily,10,FontStyle.Bold)};side.Controls.Add(groupCount,0,2);
            var groupHost=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            groups=new ListBox {Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=84,IntegralHeight=false,BackColor=Color.White};
            groups.DrawItem+=DrawGroup;groups.SelectedIndexChanged+=(s,e)=>{page=0;RenderGroup();};groupHost.Controls.Add(groups);
            groupPlaceholder=new Label {Dock=DockStyle.Top,Height=110,Text="还没有候选组\r\n\r\n扫描完成后，点击这里的\r\n分组逐一查看照片。",ForeColor=muted,Padding=new Padding(5,15,0,0)};groupHost.Controls.Add(groupPlaceholder);side.Controls.Add(groupHost,0,3);
            var guidance=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            guidance.Controls.Add(new Label {Text="由你决定保留哪张",Font=new Font(Font.FontFamily,9,FontStyle.Bold),AutoSize=true,Location=new Point(2,10)});
            guidance.Controls.Add(new Label {Text="先对照，再勾选。\r\n每组至少保留一张照片。",ForeColor=muted,AutoSize=true,Location=new Point(2,36)});
            var help=new LinkLabel {Text="使用帮助  ↗",AutoSize=true,Location=new Point(2,88),LinkColor=accent,ActiveLinkColor=accent};help.LinkClicked+=(s,e)=>ShowHelp();guidance.Controls.Add(help);side.Controls.Add(guidance,0,4);

            var main=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=7,ColumnCount=1,Margin=Padding.Empty};
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{72,106,84,58})main.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            main.RowStyles.Add(new RowStyle(SizeType.Percent,100));main.RowStyles.Add(new RowStyle(SizeType.Absolute,47));main.RowStyles.Add(new RowStyle(SizeType.Absolute,95));shell.Controls.Add(main,1,0);
            var head=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            head.Controls.Add(new Label {Text="留下值得保留的每一张",Font=new Font(Font.FontFamily,22,FontStyle.Bold),AutoSize=true,Location=new Point(0,0)});
            head.Controls.Add(new Label {Text="把相似照片放在一起，轻松对照、筛选与整理。",ForeColor=muted,AutoSize=true,Location=new Point(2,45)});
            main.Controls.Add(head,0,0);

            var settings=new Surface {Dock=DockStyle.Fill,Padding=new Padding(14,10,14,8),Margin=new Padding(0,0,0,8)};
            var grid=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=2,BackColor=Color.White,Margin=Padding.Empty};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,112));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,115));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,39));grid.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            folder=new TextBox {Dock=DockStyle.Fill,Margin=new Padding(0,7,12,0),BorderStyle=BorderStyle.FixedSingle,Font=new Font(Font.FontFamily,10),AccessibleName="照片文件夹路径"};
            browse=Button("选择文件夹",(s,e)=>ChooseFolder());browse.Dock=DockStyle.Fill;
            scan=Button("开始扫描  →",(s,e)=>StartScan());scan.Dock=DockStyle.Fill;scan.BackColor=accent;scan.ForeColor=Color.White;scan.FlatAppearance.BorderColor=accent;
            grid.Controls.Add(folder,0,0);grid.Controls.Add(browse,1,0);grid.Controls.Add(scan,2,0);
            var options=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0,2,0,0)};
            recursive=new CheckBox {Text="包含子文件夹",Checked=true,AutoSize=true,Margin=new Padding(0,6,18,0)};
            mode=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=191,Margin=new Padding(0,2,16,0),AccessibleName="相似度筛查模式"};mode.Items.AddRange(new object[]{"仅完全重复（字节相同）","严格相似（推荐）","一般相似","宽松相似（可能误报）"});mode.SelectedIndex=1;
            options.Controls.AddRange(new Control[]{recursive,mode,new Label {Text="支持 JPG / JPEG",AutoSize=true,ForeColor=muted,Margin=new Padding(0,6,0,0)}});
            grid.Controls.Add(options,0,1);grid.SetColumnSpan(options,2);
            cancel=Button("取消扫描",(s,e)=>{if(worker!=null)worker.CancelAsync();cancel.Enabled=false;status.Text="正在取消，请稍候…";});cancel.Dock=DockStyle.Fill;cancel.Height=28;cancel.Enabled=false;grid.Controls.Add(cancel,2,1);settings.Controls.Add(grid);main.Controls.Add(settings,0,1);

            var stats=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,0,0,6)};
            stats.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            for(int i=0;i<3;i++)stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));
            stats.Controls.Add(Stat("已分析照片",out readValue,false),0,0);stats.Controls.Add(Stat("候选分组",out groupValue,false),1,0);stats.Controls.Add(Stat("已勾选文件大小",out spaceValue,true),2,0);main.Controls.Add(stats,0,2);
            var title=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};groupTitle=new Label {Text="从一个照片文件夹开始",AutoSize=true,Font=new Font(Font.FontFamily,13,FontStyle.Bold),Location=new Point(0,4)};
            detail=new Label {Text="找到相似照片后，在这里查看、对照并勾选。",ForeColor=muted,Location=new Point(1,32),AutoSize=false,Height=23,Dock=DockStyle.Bottom,AutoEllipsis=true};title.Controls.Add(groupTitle);title.Controls.Add(detail);main.Controls.Add(title,0,3);

            var content=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            tiles=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoScroll=true,BackColor=BackColor,Padding=new Padding(0,1,0,1),Margin=Padding.Empty};tiles.Resize+=(s,e)=>ResizeCards();content.Controls.Add(tiles);
            dropSurface=new Surface {Dock=DockStyle.Fill,Fill=Color.White,Stroke=Color.FromArgb(208,224,219),Margin=Padding.Empty};content.Controls.Add(dropSurface);
            emptyState=new Panel {Size=new Size(520,290),BackColor=Color.White};dropSurface.Controls.Add(emptyState);
            emptyState.Controls.Add(new FolderArtwork {Bounds=new Rectangle(204,0,112,91)});
            emptyTitle=new Label {Text="把照片文件夹拖到这里",Bounds=new Rectangle(0,100,520,33),TextAlign=ContentAlignment.MiddleCenter,Font=new Font(Font.FontFamily,16,FontStyle.Bold)};
            emptyCopy=new Label {Text="或选择文件夹，开始发现重复与相似的照片",Bounds=new Rectangle(0,140,520,26),TextAlign=ContentAlignment.MiddleCenter,ForeColor=muted};
            emptyChoose=Button("选择照片文件夹",(s,e)=>ChooseFolder());emptyChoose.Bounds=new Rectangle(176,178,168,40);emptyChoose.BackColor=accent;emptyChoose.ForeColor=Color.White;emptyChoose.FlatAppearance.BorderColor=accent;
            steps=new FlowLayoutPanel {Bounds=new Rectangle(20,250,480,30),WrapContents=false};
            foreach(string step in new[]{"01  选择文件夹","02  扫描相似照片","03  看图决定去留"})steps.Controls.Add(new Label {Text=step,Width=156,Height=25,TextAlign=ContentAlignment.MiddleCenter,ForeColor=muted,Margin=Padding.Empty});
            emptyState.Controls.AddRange(new Control[]{emptyTitle,emptyCopy,emptyChoose,steps});
            dropSurface.Resize+=(s,e)=>LayoutEmpty();main.Controls.Add(content,0,4);
            EnableFolderDrop(this);EnableFolderDrop(folder);EnableFolderDrop(dropSurface);EnableFolderDrop(emptyState);
            foreach(Control c in emptyState.Controls)EnableFolderDrop(c);

            var actions=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,8,0,0)};actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,217));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var selectionActions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            suggest=Button("勾选本组其余照片",(s,e)=>Suggest());suggest.Width=155;clear=Button("清空勾选",(s,e)=>{selected.Clear();RenderGroup();});clear.Width=97;selectionActions.Controls.AddRange(new Control[]{suggest,clear});actions.Controls.Add(selectionActions,0,0);
            var pages=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,FlowDirection=FlowDirection.LeftToRight};
            prev=Button("‹  上一页",(s,e)=>{page--;RenderGroup();});next=Button("下一页  ›",(s,e)=>{page++;RenderGroup();});prev.Width=76;next.Width=76;pageText=new Label {Text="—",Width=45,Height=34,TextAlign=ContentAlignment.MiddleCenter,Margin=Padding.Empty};pages.Controls.AddRange(new Control[]{prev,pageText,next});actions.Controls.Add(pages,1,0);main.Controls.Add(actions,0,5);

            var foot=new Surface {Dock=DockStyle.Fill,Padding=new Padding(12,8,12,8),Margin=new Padding(0,2,0,0)};
            var footer=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=2,BackColor=Color.White,Margin=Padding.Empty};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,422));footer.RowStyles.Add(new RowStyle(SizeType.Absolute,40));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            selection=new Label {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Font=new Font(Font.FontFamily,10,FontStyle.Bold)};footer.Controls.Add(selection,0,0);
            var output=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty};
            remove=Button("移入回收站",(s,e)=>DeleteSelected());remove.Width=114;remove.BackColor=Color.FromArgb(168,58,62);remove.ForeColor=Color.White;remove.FlatAppearance.BorderColor=remove.BackColor;
            export=Button("导出候选副本",(s,e)=>Export());export.Width=119;report=Button("导出清单",(s,e)=>Report());report.Width=83;issues=Button("日志",(s,e)=>ShowIssues());issues.Width=65;output.Controls.AddRange(new Control[]{remove,export,report,issues});footer.Controls.Add(output,1,0);
            status=new Label {Dock=DockStyle.Fill,ForeColor=muted,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Text="就绪 · 选择文件夹后开始扫描"};progress=new ProgressBar {Dock=DockStyle.Fill,Margin=new Padding(8,10,4,9)};footer.Controls.Add(status,0,1);footer.Controls.Add(progress,1,1);foot.Controls.Add(footer);main.Controls.Add(foot,0,6);
            summary=new Label(); // Carries the full scan summary for accessibility and diagnostics.
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;MessageBox.Show(this,"请先取消扫描，或等待当前操作完成。","操作进行中");}};
            UpdateActions();
        }
        Button Button(string text,EventHandler click) {var b=new ActionButton {Text=text,Width=100,Height=34,BackColor=Color.White,ForeColor=ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,8,0)};b.FlatAppearance.BorderColor=Color.FromArgb(216,228,224);b.Click+=click;return b;}
        Control Stat(string title,out Label value,bool highlighted) {
            var box=new Surface {Dock=DockStyle.Fill,Margin=new Padding(0,0,9,0),Fill=highlighted?Color.FromArgb(230,243,236):Color.White,Stroke=highlighted?Color.FromArgb(208,230,219):Color.FromArgb(223,231,232),Padding=new Padding(15,8,12,7)};
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=box.Fill,Margin=Padding.Empty};layout.RowStyles.Add(new RowStyle(SizeType.Absolute,22));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.Controls.Add(new Label {Text=title,Dock=DockStyle.Fill,ForeColor=muted,Margin=Padding.Empty},0,0);value=new Label {Text="—",Dock=DockStyle.Fill,Font=new Font("Segoe UI",20,FontStyle.Bold),ForeColor=highlighted?accent:ink,Margin=Padding.Empty};layout.Controls.Add(value,0,1);box.Controls.Add(layout);return box;
        }
        void EnableFolderDrop(Control target) {
            target.AllowDrop=true;
            target.DragEnter+=(s,e)=>{if(busy)return;var files=e.Data.GetData(DataFormats.FileDrop) as string[];e.Effect=files!=null&&files.Length==1&&Directory.Exists(files[0])?DragDropEffects.Copy:DragDropEffects.None;};
            target.DragDrop+=(s,e)=>{if(busy)return;var files=e.Data.GetData(DataFormats.FileDrop) as string[];if(files!=null&&files.Length==1&&Directory.Exists(files[0]))SetFolder(files[0]);};
        }
        void SetFolder(string path) {folder.Text=path;status.Text="文件夹已就绪 · 点击“开始扫描”";if(result==null){emptyTitle.Text="文件夹已就绪";emptyCopy.Text=System.IO.Path.GetFileName(path.TrimEnd('\\'));}}
        void ShowHelp() {MessageBox.Show(this,"1. 选择或拖入照片文件夹，点击“开始扫描”。\r\n2. 在左侧选择分组，点击“大图对照”比较照片。\r\n3. 勾选不需要的照片，可导出副本或移入回收站。\r\n\r\n“勾选本组其余照片”按像素数和文件大小留下一张；请自行确认画质。\r\n每组必须至少保留一张。导出副本会复制所有候选组。\r\n\r\n相似筛查可能误报或漏检，可切换匹配模式后重新扫描。", "照片筛查 · 使用帮助",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        void ResizeCards() {
            if(tiles==null || tiles.ClientSize.Width<1 || resizingCards || rendering)return;
            resizingCards=true;
            try {
            int available=tiles.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-5;
            int columns=Math.Max(1,available/272);int width=Math.Max(235,available/columns-12);
            tiles.SuspendLayout();foreach(Control card in tiles.Controls)card.Width=width;tiles.ResumeLayout();
            } finally {resizingCards=false;}
        }
        void UpdateEmptyState() {
            bool have=result!=null&&result.Groups.Count>0;
            dropSurface.Visible=!have;tiles.Visible=have;groupPlaceholder.Visible=!have;
            if(have)return;
            if(busy){groupTitle.Text="正在扫描照片";emptyTitle.Text="正在整理相似画面…";emptyCopy.Text="扫描在本地进行，大文件夹可能需要一点时间。";emptyChoose.Visible=false;steps.Visible=false;}
            else if(result!=null&&result.Cancelled){groupTitle.Text="扫描已取消";emptyTitle.Text="随时可以重新开始";emptyCopy.Text="调整文件夹或匹配模式，再点击“开始扫描”。";emptyChoose.Visible=true;steps.Visible=true;}
            else if(result!=null){groupTitle.Text="扫描已完成";emptyTitle.Text="没有剩余候选组";emptyCopy.Text="可以换一个文件夹，或调整匹配模式后重新扫描。";emptyChoose.Visible=true;steps.Visible=true;}
            else {groupTitle.Text="从一个照片文件夹开始";emptyChoose.Visible=true;steps.Visible=true;}
            LayoutEmpty();
        }
        void LayoutEmpty() {
            if(emptyState==null)return;
            bool compact=dropSurface.Height<275;
            emptyState.Height=compact?150:290;
            emptyState.Controls.OfType<FolderArtwork>().First().Visible=!compact;
            emptyTitle.Top=compact?7:100;emptyCopy.Top=compact?47:140;emptyChoose.Top=compact?88:178;
            steps.Visible=!compact&&!busy;
            emptyState.Location=new Point(Math.Max(0,(dropSurface.Width-emptyState.Width)/2),Math.Max(0,(dropSurface.Height-emptyState.Height)/2));
        }

        public static string SizeText(long bytes) {return bytes>=1073741824 ? (bytes/1073741824.0).ToString("0.00")+" GB" : bytes>=1048576 ? (bytes/1048576.0).ToString("0.0")+" MB" : (bytes/1024.0).ToString("0.0")+" KB";}
        void ChooseFolder() {using(var d=new FolderBrowserDialog {Description="选择需要筛查的照片文件夹",ShowNewFolderButton=false}) {if(Directory.Exists(folder.Text))d.SelectedPath=folder.Text;if(d.ShowDialog(this)==DialogResult.OK)SetFolder(d.SelectedPath);}}
        void SetBusy(bool value) {busy=value;browse.Enabled=folder.Enabled=recursive.Enabled=mode.Enabled=scan.Enabled=!value;groups.Enabled=tiles.Enabled=!value;UpdateActions();}
        void UpdateActions() {
            bool have=result!=null && result.Groups.Count>0;
            export.Enabled=report.Enabled=have&&!busy;remove.Enabled=selected.Count>0&&!busy;suggest.Enabled=have&&groups.SelectedIndex>=0&&!busy;clear.Enabled=selected.Count>0&&!busy;
            issues.Enabled=result!=null&&!busy;prev.Enabled=have&&page>0&&!busy;next.Enabled=have&&groups.SelectedIndex>=0&&(page+1)*PageSize<result.Groups[groups.SelectedIndex].Photos.Count&&!busy;
            long bytes=result==null?0:result.Groups.SelectMany(g=>g.Photos).Where(p=>selected.Contains(p.Path)).Sum(p=>p.Bytes);
            selection.Text="已勾选 "+selected.Count+" 张";
            readValue.Text=result==null?(busy?"…":"0"):result.Read.ToString("N0");
            groupValue.Text=result==null?"0":result.Groups.Count.ToString("N0");
            spaceValue.Text=SizeText(bytes);groupCount.Text="相似分组  /  "+(result==null?0:result.Groups.Count);
            UpdateEmptyState();groups.Invalidate();
        }
        void StartScan() {
            string path=folder.Text.Trim().Trim('"');if(!Directory.Exists(path)){MessageBox.Show(this,"请选择有效的文件夹。","无法扫描");return;}
            folder.Text=path;bool rec=recursive.Checked;int sensitivity=mode.SelectedIndex;
            result=null;selected.Clear();groups.Items.Clear();ClearTiles();summary.Text="正在查找和分析照片…";detail.Text="扫描结果将在完成后显示。大文件夹可能需要一些时间。";SetBusy(true);cancel.Enabled=true;progress.Value=0;
            worker=new BackgroundWorker {WorkerReportsProgress=true,WorkerSupportsCancellation=true};int last=-1;DateTime tick=DateTime.MinValue;
            worker.DoWork+=(s,e)=>{e.Result=Engine.Scan(path,rec,sensitivity,()=>worker.CancellationPending,(v,t)=>{if(v!=last || (DateTime.UtcNow-tick).TotalMilliseconds>160){last=v;tick=DateTime.UtcNow;worker.ReportProgress(v,t);}});};
            worker.ProgressChanged+=(s,e)=>{progress.Value=e.ProgressPercentage;status.Text=(string)e.UserState;};
            worker.RunWorkerCompleted+=(s,e)=> {
                cancel.Enabled=false;
                if(e.Error!=null){status.Text="扫描失败";emptyTitle.Text="暂时无法完成扫描";emptyCopy.Text="检查文件夹是否可以访问，然后重试。";MessageBox.Show(this,e.Error.Message,"扫描失败");}
                else {result=(ScanResult)e.Result;if(result.Cancelled){summary.Text="扫描已取消，未生成分组。";status.Text="可重新选择文件夹或开始扫描";progress.Value=0;}
                else {int n=result.Groups.Sum(g=>g.Photos.Count);summary.Text="发现 "+result.Groups.Count+" 组候选 · "+n+" 张照片 · 已分析 "+result.Read+" / "+result.Found+" 张";
                    status.Text=result.Errors.Count==0?"完成 · 请检查内容后手动勾选；原图没有改变":"完成 · "+result.Errors.Count+" 项未能读取，请查看扫描日志";
                    RefreshGroups();if(result.Groups.Count==0) detail.Text="没有找到符合条件的候选组，可调整匹配模式后重新扫描。";
                }}worker.Dispose();worker=null;SetBusy(false);
            };worker.RunWorkerAsync();
        }
        void RefreshGroups() {groups.BeginUpdate();groups.Items.Clear();if(result!=null)for(int i=0;i<result.Groups.Count;i++)groups.Items.Add(i);groups.EndUpdate();if(groups.Items.Count>0)groups.SelectedIndex=0;else {ClearTiles();detail.Text="没有剩余候选组。";}UpdateActions();}
        void DrawGroup(object sender,DrawItemEventArgs e) {
            if(e.Index<0 || result==null || e.Index>=result.Groups.Count)return;
            bool active=(e.State&DrawItemState.Selected)!=0;var g=result.Groups[e.Index];var area=e.Bounds;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillRectangle(Brushes.White,area);
            using(var p=Shape.Round(new Rectangle(area.X,area.Y+3,area.Width-3,area.Height-7),10))using(var b=new SolidBrush(active?Color.FromArgb(229,242,235):Color.FromArgb(248,250,249)))e.Graphics.FillPath(b,p);
            using(var f=new Font("Segoe UI",10,FontStyle.Bold))TextRenderer.DrawText(e.Graphics,(e.Index+1).ToString("00"),f,new Rectangle(area.X+9,area.Y+15,30,26),accent,TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter);
            using(var titleFont=new Font(Font.FontFamily,10,FontStyle.Bold))TextRenderer.DrawText(e.Graphics,g.Photos.Count+" 张照片",titleFont,new Point(area.X+44,area.Y+15),ink);
            TextRenderer.DrawText(e.Graphics,g.Exact?"文件完全相同":"画面相似",Font,new Point(area.X+44,area.Y+42),muted);
            int count=g.Photos.Count(p=>selected.Contains(p.Path));
            if(count>0)using(var b=new SolidBrush(accent))e.Graphics.FillEllipse(b,area.Right-17,area.Y+18,6,6);
        }
        void ClearTiles() {while(tiles.Controls.Count>0){var c=tiles.Controls[0];tiles.Controls.Remove(c);c.Dispose();}}
        void RenderGroup() {
            rendering=true;tiles.SuspendLayout();ClearTiles();
            if(result!=null && groups.SelectedIndex>=0) {
                var g=result.Groups[groups.SelectedIndex];var best=g.Best;int pages=(g.Photos.Count+PageSize-1)/PageSize;page=Math.Max(0,Math.Min(page,pages-1));
                groupTitle.Text="第 "+(groups.SelectedIndex+1).ToString("00")+" 组  ·  "+g.Photos.Count+" 张"+(g.Exact?"完全相同的照片":"相似照片");
                detail.Text=g.Exact?"文件内容完全相同。查看照片后，勾选需要移除的副本。":"双击照片或点击“大图对照”，确认画面细节后再勾选。";
                foreach(var p in g.Photos.Skip(page*PageSize).Take(PageSize))AddTile(g,p,best);
                pageText.Text=(page+1)+" / "+pages;
            }else pageText.Text="";
            tiles.ResumeLayout();rendering=false;ResizeCards();UpdateActions();
        }
        void AddTile(PhotoGroup group,Photo p,Photo best) {
            bool anchor=p==group.Photos[0];var card=new Surface {Width=290,Height=298,Margin=new Padding(0,0,12,12),Padding=new Padding(10),Radius=12};
            var pic=new PictureBox {Location=new Point(10,10),Size=new Size(270,170),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(239,243,241),Cursor=Cursors.Hand};
            try {pic.Image=Engine.Thumbnail(p.Path,680,400);}catch {pic.BackColor=Color.MistyRose;}
            pic.Disposed+=(s,e)=>{if(pic.Image!=null)pic.Image.Dispose();};
            EventHandler preview=(s,e)=>{using(var window=new Preview(group.Photos[0],anchor?null:p))window.ShowDialog(this);};pic.DoubleClick+=preview;
            var check=new CheckBox {Text=System.IO.Path.GetFileName(p.Path),AutoEllipsis=true,Location=new Point(12,189),Size=new Size(266,24),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,Checked=selected.Contains(p.Path),BackColor=Color.White};
            var badge=new Label {Text=p==best?"建议保留 · 参考图":p.Sha==group.Photos[0].Sha?"完全相同":"相似候选",AutoSize=false,Size=new Size(142,25),Location=new Point(6,6),TextAlign=ContentAlignment.MiddleCenter,BackColor=Color.White,ForeColor=accent,Font=new Font(Font.FontFamily,8,FontStyle.Bold)};pic.Controls.Add(badge);
            check.CheckedChanged+=(s,e)=>{if(rendering)return;if(check.Checked)selected.Add(p.Path);else selected.Remove(p.Path);card.Stroke=check.Checked?accent:Color.FromArgb(223,231,232);badge.Text=check.Checked?"已勾选 · 待移除":p==best?"建议保留 · 参考图":p.Sha==group.Photos[0].Sha?"完全相同":"相似候选";badge.BackColor=check.Checked?accent:Color.White;badge.ForeColor=check.Checked?Color.White:accent;card.Invalidate();UpdateActions();};
            if(check.Checked){card.Stroke=accent;badge.Text="已勾选 · 待移除";badge.BackColor=accent;badge.ForeColor=Color.White;}
            var meta=new Label {Location=new Point(12,218),Size=new Size(266,22),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,ForeColor=muted,Text=p.Width+" × "+p.Height+"  ·  "+SizeText(p.Bytes),BackColor=Color.White};
            var compare=Button(anchor?"查看参考大图":"大图对照",preview);compare.Location=new Point(11,251);compare.Width=116;compare.Height=32;compare.BackColor=Color.FromArgb(239,247,243);compare.ForeColor=accent;
            var location=new LinkLabel {Location=new Point(177,258),Size=new Size(101,22),Anchor=AnchorStyles.Top|AnchorStyles.Right,Text="定位原图  ↗",TextAlign=ContentAlignment.MiddleRight,BackColor=Color.White,LinkColor=muted};location.LinkClicked+=(s,e)=>{try{System.Diagnostics.Process.Start("explorer.exe","/select,\""+p.Path+"\"");}catch(Exception ex){MessageBox.Show(this,ex.Message);}};
            var tip=new ToolTip();tip.SetToolTip(check,p.Path);tip.SetToolTip(badge,p==best?"按像素数、文件大小排序，不代表实际画质。":"与参考图的感知特征差异："+Engine.Distance(p.PHash,group.Photos[0].PHash)+" / 63");card.Disposed+=(s,e)=>tip.Dispose();
            card.Controls.AddRange(new Control[]{pic,check,meta,compare,location});tiles.Controls.Add(card);
        }
        void Suggest() {if(result==null||groups.SelectedIndex<0)return;var g=result.Groups[groups.SelectedIndex];foreach(var p in g.Photos) {if(p!=g.Best)selected.Add(p.Path);else selected.Remove(p.Path);}RenderGroup();}
        void Report() {using(var d=new SaveFileDialog {Filter="CSV 清单|*.csv",FileName="重复照片清单.csv"})if(d.ShowDialog(this)==DialogResult.OK)try{Engine.Report(result,d.FileName);status.Text="清单已保存："+d.FileName;}catch(Exception ex){MessageBox.Show(this,ex.Message,"导出失败");}}
        void ShowIssues() {
            using(var f=new Form {Text="扫描日志",Width=820,Height=520,StartPosition=FormStartPosition.CenterParent}) {
                f.Controls.Add(new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=Font,Text="扫描目录："+result.Root+"\r\n发现 JPEG："+result.Found+"；成功分析："+result.Read+"\r\n\r\n"+(result.Errors.Count==0?"无读取错误。":string.Join("\r\n\r\n",result.Errors))});f.ShowDialog(this);
            }
        }
        void Export() {
            using(var d=new FolderBrowserDialog {Description="选择副本保存位置（按组复制；不会移动原图）"}) {
                if(d.ShowDialog(this)!=DialogResult.OK)return;
                string path=d.SelectedPath;SetBusy(true);progress.Value=0;
                var job=new BackgroundWorker {WorkerReportsProgress=true};job.DoWork+=(s,e)=>e.Result=Engine.Export(result,path,(v,t)=>job.ReportProgress(v,t));
                job.ProgressChanged+=(s,e)=>{progress.Value=e.ProgressPercentage;status.Text=(string)e.UserState;};
                job.RunWorkerCompleted+=(s,e)=>{SetBusy(false);if(e.Error!=null){status.Text="导出中断；已复制文件会保留";MessageBox.Show(this,e.Error.Message,"导出失败");}else{status.Text="副本已保存："+(string)e.Result;MessageBox.Show(this,"已按组复制照片，并生成原始路径对照表。\r\n"+(string)e.Result,"导出完成");}job.Dispose();};job.RunWorkerAsync();
            }
        }
        void DeleteSelected() {
            var photos=result.Groups.SelectMany(g=>g.Photos).Where(p=>selected.Contains(p.Path)).ToList();if(photos.Count==0)return;
            if(result.Groups.Any(g=>g.Photos.All(p=>selected.Contains(p.Path)))) {MessageBox.Show(this,"有一组的全部照片都被勾选了。请至少保留一张，再继续。","请保留原图");return;}
            if(MessageBox.Show(this,"将把勾选的 "+photos.Count+" 张照片移入 Windows 回收站。\r\n请确认已经逐张检查画面。\r\n\r\n如果 Windows 提示无法回收、需要永久删除，请选择取消。", "确认移入回收站",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
            SetBusy(true);var errors=new List<string>();int done=0;
            try {
                foreach(var p in photos) {
                    // Verify a surviving member, not just its path, before allowing each removal.
                    var group=result.Groups.First(g=>g.Photos.Contains(p));
                    try {
                        if(!group.Photos.Any(q=>!selected.Contains(q.Path)&&Engine.Unchanged(q))) throw new IOException("未找到未变化的保留照片，请重新扫描");
                        if(!Engine.Unchanged(p)) throw new IOException("照片在扫描后发生变化，请重新扫描");
                        Recycle.Send(p.Path,Handle);group.Photos.Remove(p);selected.Remove(p.Path);done++;
                    }catch(OperationCanceledException){break;}catch(Exception ex){errors.Add(p.Path+"："+ex.Message);}
                }
                // A removed reference can invalidate the old star grouping; rebuild it.
                result.Groups=Engine.Group(result.Groups.SelectMany(g=>g.Photos).ToList(),result.Mode,()=>false);
                selected.IntersectWith(result.Groups.SelectMany(g=>g.Photos).Select(p=>p.Path));RefreshGroups();
                summary.Text="剩余 "+result.Groups.Count+" 组候选 · "+result.Groups.Sum(g=>g.Photos.Count)+" 张照片";status.Text="已移入回收站 "+done+" 张";
                if(errors.Count>0){result.Errors.AddRange(errors);MessageBox.Show(this,"有 "+errors.Count+" 张未处理。详情见扫描日志。\r\n"+errors[0],"部分照片未处理");}
            } finally {SetBusy(false);}
        }
        public void LoadDemo(ScanResult demo) {result=demo;folder.Text=demo.Root;summary.Text="发现 "+demo.Groups.Count+" 组候选 · "+demo.Groups.Sum(g=>g.Photos.Count)+" 张照片";RefreshGroups();}
    }
    static class Program {
        [STAThread] static void Main() {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"照片筛查遇到问题");
            Application.Run(new MainForm());
        }
    }
}

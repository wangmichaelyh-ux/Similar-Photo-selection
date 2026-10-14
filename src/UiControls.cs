using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhotoSieve {
    public static class Shape {
        public static GraphicsPath Round(Rectangle r, int radius) {
            int d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
            var path=new GraphicsPath();
            if(d<2){path.AddRectangle(r);return path;}
            path.AddArc(r.X,r.Y,d,d,180,90);path.AddArc(r.Right-d,r.Y,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.X,r.Bottom-d,d,d,90,90);path.CloseFigure();return path;
        }
    }
    public class Surface : Panel {
        public Color Fill=Color.White;
        public Color Stroke=Color.FromArgb(223,231,232);
        public int Radius=14;
        public Surface(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor|ControlStyles.ResizeRedraw,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var p=Shape.Round(new Rectangle(0,0,Width-1,Height-1),Radius))using(var b=new SolidBrush(Fill))using(var pen=new Pen(Stroke)) {e.Graphics.FillPath(b,p);e.Graphics.DrawPath(pen,p);}
            base.OnPaint(e);
        }
    }
    public class ActionButton : Button {
        bool over;
        public ActionButton(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;}
        protected override void OnMouseEnter(EventArgs e){over=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){over=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            Color fill=Enabled?BackColor:Color.FromArgb(234,239,239);
            if(Enabled && over)fill=ControlPaint.Light(fill,.10f);
            e.Graphics.Clear(Parent==null?Color.White:Parent.BackColor.A==0?Color.White:Parent.BackColor);
            using(var p=Shape.Round(new Rectangle(0,0,Width-1,Height-1),8))using(var b=new SolidBrush(fill))using(var pen=new Pen(Enabled?FlatAppearance.BorderColor:Color.FromArgb(228,234,234))){e.Graphics.FillPath(b,p);e.Graphics.DrawPath(pen,p);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?ForeColor:Color.FromArgb(146,160,163),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10));
        }
    }
    public sealed class FolderArtwork : Control {
        public FolderArtwork(){SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e){
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=Math.Min(Width/160f,Height/130f);g.TranslateTransform((Width-160*scale)/2,(Height-130*scale)/2);g.ScaleTransform(scale,scale);
            using(var b=new SolidBrush(Color.FromArgb(227,242,238)))g.FillEllipse(b,16,0,128,128);
            using(var p=Shape.Round(new Rectangle(42,20,90,73),9))using(var b=new SolidBrush(Color.FromArgb(153,203,190)))g.FillPath(b,p);
            using(var p=Shape.Round(new Rectangle(24,39,101,76),9))using(var b=new SolidBrush(Color.White))using(var pen=new Pen(Color.FromArgb(17,112,98),2)){g.FillPath(b,p);g.DrawPath(pen,p);}
            using(var b=new SolidBrush(Color.FromArgb(242,184,84)))g.FillEllipse(b,92,52,16,16);
            using(var b=new SolidBrush(Color.FromArgb(17,112,98)))g.FillPolygon(b,new[]{new Point(34,101),new Point(61,65),new Point(82,89),new Point(95,77),new Point(116,101)});
        }
    }
}

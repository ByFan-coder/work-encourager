using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace WorkEncourager {
public class PauseRecord {
    public DateTime Start;
    public DateTime End; // MinValue means still paused.
}
public class Settings {
    public decimal MonthlyPay = 10000;
    [System.Xml.Serialization.XmlElement("DailyPay")] public decimal LegacyDailyPay;
    public bool ShouldSerializeLegacyDailyPay() { return false; }
    public List<DayOverride> Calendar = new List<DayOverride>();
    public int StartMinute = 570, EndMinute = 1095;
    public bool Lunch = true;
    public int LunchStart = 720, LunchEnd = 765;
    public bool[] Days = new bool[] {false,true,true,true,true,true,false};
    public int AmountColorArgb = Color.FromArgb(255,91,88).ToArgb();
    public bool AlwaysOnTop;
}
public class DayOverride {
    public DateTime Date;
    public bool Workday;
}
public class AppData {
    public Settings Settings = new Settings();
    public List<PauseRecord> Pauses = new List<PauseRecord>();
    public List<OvertimeRecord> Overtime = new List<OvertimeRecord>();
}
public class OvertimeRecord {
    public DateTime StartUtc, EndUtc, LocalDay;
    public int Category;
    public decimal HourlyRate;
}
public static class OvertimeEngine {
    public static readonly decimal[] Multipliers={0.75m,1m,1.5m};
    public static OvertimeRecord Running(AppData d) {return d.Overtime.FindLast(delegate(OvertimeRecord r){return r.EndUtc==DateTime.MinValue;});}
    public static OvertimeRecord Start(AppData d,int category,DateTime utc,DateTime local) {
        if(Running(d)!=null)throw new InvalidOperationException("已有加班正在计时");
        if(category<0 || category>2)throw new ArgumentOutOfRangeException("category");
        DateTime baseDay=Engine.ShiftDay(d.Settings,local);
        OvertimeRecord r=new OvertimeRecord {StartUtc=utc,LocalDay=baseDay.Date,Category=category,HourlyRate=Engine.DailyPay(d.Settings,baseDay)/(decimal)(Engine.Planned(d.Settings)/3600)};
        d.Overtime.Add(r);return r;
    }
    public static double Seconds(OvertimeRecord r,DateTime utc) {return Math.Max(0,((r.EndUtc==DateTime.MinValue?utc:r.EndUtc)-r.StartUtc).TotalSeconds);}
    public static decimal Hours(OvertimeRecord r,DateTime utc) {return (decimal)Math.Floor(Seconds(r,utc)/1800)*0.5m;}
    public static decimal Pay(OvertimeRecord r,DateTime utc) {return Hours(r,utc)*r.HourlyRate*Multipliers[r.Category];}
    public static decimal Total(AppData d,DateTime day,DateTime utc,bool month) {
        decimal amount=0;
        foreach(OvertimeRecord r in d.Overtime) if(month ? r.LocalDay.Year==day.Year && r.LocalDay.Month==day.Month:r.LocalDay.Date==day.Date)amount+=Pay(r,utc);
        return amount;
    }
    public static bool Valid(List<OvertimeRecord> records) {
        if(records==null)return false;int open=0;
        foreach(OvertimeRecord r in records) {
            if(r==null || r.StartUtc==DateTime.MinValue || r.LocalDay==DateTime.MinValue || r.LocalDay!=r.LocalDay.Date || r.HourlyRate<0 || r.Category<0 || r.Category>2)return false;
            if(r.EndUtc==DateTime.MinValue)open++;else if(r.EndUtc<r.StartUtc)return false;
        }
        return open<=1;
    }
}
public class Span {
    public DateTime A, B;
    public Span(DateTime a, DateTime b) { A=a; B=b; }
}
public static class Engine {
    public static bool Validate(Settings s) {
        if(s == null || s.Days == null || s.Days.Length != 7 || !Array.Exists(s.Days, delegate(bool d){return d;})) return false;
        if(s.MonthlyPay <= 0 || s.MonthlyPay > 31000000 || Color.FromArgb(s.AmountColorArgb).A != 255 || s.Calendar==null) return false;
        HashSet<DateTime> dates=new HashSet<DateTime>();
        foreach(DayOverride item in s.Calendar) if(item==null || item.Date!=item.Date.Date || !dates.Add(item.Date)) return false;
        if(s.StartMinute < 0 || s.StartMinute > 1439 || s.EndMinute < 0 || s.EndMinute > 1439 || s.StartMinute == s.EndMinute) return false;
        if(s.Lunch) {
            if(s.LunchStart < 0 || s.LunchStart > 1439 || s.LunchEnd < 0 || s.LunchEnd > 1439) return false;
            int duration=(s.EndMinute-s.StartMinute+1440)%1440;
            int a=(s.LunchStart-s.StartMinute+1440)%1440, b=(s.LunchEnd-s.StartMinute+1440)%1440;
            if(a>=b || b>duration || b-a>=duration) return false;
        }
        return true;
    }
    public static List<Span> Blocks(Settings s, DateTime day) {
        List<Span> r=new List<Span>(); day=day.Date;
        if(!IsWorkday(s,day)) return r;
        DateTime a=day.AddMinutes(s.StartMinute);
        DateTime b=day.AddMinutes(s.EndMinute+(s.EndMinute<=s.StartMinute ? 1440:0));
        if(!s.Lunch) { r.Add(new Span(a,b)); return r; }
        DateTime l=a.AddMinutes((s.LunchStart-s.StartMinute+1440)%1440);
        DateTime e=a.AddMinutes((s.LunchEnd-s.StartMinute+1440)%1440);
        if(l>a) r.Add(new Span(a,l));
        if(e<b) r.Add(new Span(e,b));
        return r;
    }
    public static bool IsWorkday(Settings s, DateTime day) {
        DayOverride item=s.Calendar.Find(delegate(DayOverride x){return x.Date.Date==day.Date;});
        return item==null ? s.Days[(int)day.DayOfWeek]:item.Workday;
    }
    public static int Workdays(Settings s, DateTime month) {
        int count=0;
        for(int n=1;n<=DateTime.DaysInMonth(month.Year,month.Month);n++) if(IsWorkday(s,new DateTime(month.Year,month.Month,n))) count++;
        return count;
    }
    public static decimal DailyPay(Settings s, DateTime day) {
        int count=Workdays(s,day);
        return count==0 ? 0:s.MonthlyPay/count;
    }
    public static DateTime ShiftDay(Settings s, DateTime now) {
        if(s.EndMinute<=s.StartMinute && now.TimeOfDay.TotalMinutes<s.EndMinute) return now.Date.AddDays(-1);
        return now.Date;
    }
    public static double Planned(Settings s) {
        double n=(s.EndMinute-s.StartMinute+1440)%1440;
        if(s.Lunch) n-=(s.LunchEnd-s.LunchStart+1440)%1440;
        return n*60;
    }
    public static double Seconds(AppData data, DateTime day, DateTime until) {
        double total=0;
        foreach(Span block in Blocks(data.Settings,day)) {
            DateTime end=until<block.B ? until:block.B;
            if(end<=block.A) continue;
            double lost=0;
            // Merge intersecting pause intervals so a damaged/edited file cannot subtract twice.
            List<Span> cuts=new List<Span>();
            foreach(PauseRecord p in data.Pauses) {
                DateTime a=p.Start>block.A ? p.Start:block.A;
                DateTime b=p.End==DateTime.MinValue ? until:p.End;
                if(b>end) b=end;
                if(b>a) cuts.Add(new Span(a,b));
            }
            cuts.Sort(delegate(Span x,Span y){return x.A.CompareTo(y.A);});
            DateTime cursor=block.A;
            foreach(Span c in cuts) { DateTime a=c.A>cursor ? c.A:cursor; if(c.B>a) lost+=(c.B-a).TotalSeconds; if(c.B>cursor)cursor=c.B; }
            total+=Math.Max(0,(end-block.A).TotalSeconds-lost);
        }
        return total;
    }
    public static decimal Pay(AppData d, DateTime day, DateTime now) { return DailyPay(d.Settings,day)*(decimal)(Seconds(d,day,now)/Planned(d.Settings)); }
    public static decimal Month(AppData d, DateTime day, DateTime now) {
        decimal total=0;
        for(DateTime date=new DateTime(day.Year,day.Month,1);date<=day.Date;date=date.AddDays(1)) total+=Pay(d,date,now);
        return total;
    }
    public static PauseRecord OpenPause(AppData d) { return d.Pauses.FindLast(delegate(PauseRecord p){return p.End==DateTime.MinValue;}); }
    public static bool Active(Settings s,DateTime day,DateTime now) { return Blocks(s,day).Exists(delegate(Span b){return now>=b.A && now<b.B;}); }
}
public static class Store {
    public static string PathName=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data.xml");
    public static AppData Load() {
        if(!File.Exists(PathName)) return new AppData();
        using(FileStream f=File.OpenRead(PathName)) {
            AppData d=(AppData)new XmlSerializer(typeof(AppData)).Deserialize(f);
            if(d.Settings!=null && d.Settings.LegacyDailyPay>0) {
                if(d.Settings.Days==null || d.Settings.Days.Length!=7 || d.Settings.Calendar==null) throw new InvalidDataException("旧版排班无效");
                d.Settings.MonthlyPay=d.Settings.LegacyDailyPay*Engine.Workdays(d.Settings,Engine.ShiftDay(d.Settings,DateTime.Now));
                d.Settings.LegacyDailyPay=0;
            }
            if(!Engine.Validate(d.Settings) || d.Pauses==null || !OvertimeEngine.Valid(d.Overtime) || d.Pauses.Exists(delegate(PauseRecord p){return p==null || (p.End!=DateTime.MinValue && p.End<p.Start);})) throw new InvalidDataException("配置内容无效");
            return d;
        }
    }
    public static void Save(AppData data) {
        string temp=PathName+".tmp";
        using(FileStream f=new FileStream(temp,FileMode.Create,FileAccess.Write)) {new XmlSerializer(typeof(AppData)).Serialize(f,data); f.Flush(true);}
        if(File.Exists(PathName)) File.Copy(PathName,PathName+".bak",true);
        // All moves stay in the application folder; do not require ACL replacement privileges.
        if(File.Exists(PathName)) File.Delete(PathName);
        try { File.Move(temp,PathName); }
        catch { if(!File.Exists(PathName) && File.Exists(PathName+".bak")) File.Copy(PathName+".bak",PathName); throw; }
    }
}
public class FlatButton : Button {
    public Color Fill=Color.FromArgb(95,103,115);
    public FlatButton() {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque,true);
        FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
        Cursor=Cursors.Hand;ForeColor=Color.White;TabStop=true;
    }
    protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Parent==null ? BackColor:Parent.BackColor); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Parent==null ? BackColor:Parent.BackColor);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(GraphicsPath p=MainForm.Round(new RectangleF(0,0,Width,Height),8)) using(SolidBrush b=new SolidBrush(ClientRectangle.Contains(PointToClient(MousePosition)) ? ControlPaint.Light(Fill,.1f):Fill)) e.Graphics.FillPath(b,p);
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        if(Focused && ShowFocusCues) using(GraphicsPath p=MainForm.Round(new RectangleF(2,2,Width-4,Height-4),6)) using(Pen pen=new Pen(Color.FromArgb(115,182,242))) e.Graphics.DrawPath(pen,p);
    }
}
public class MainForm : Form {
    AppData data;
    System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    FlatButton pause=new FlatButton(), settings=new FlatButton(), overtime=new FlatButton(), minimize=new FlatButton(), close=new FlatButton();
    FlatButton overtimeType=new FlatButton();
    ContextMenuStrip overtimeMenu=new ContextMenuStrip();
    int overtimeCategory;
    Point? dragOffset;
    float scale=1;
    static Color Bg=Color.FromArgb(45,51,61), Muted=Color.FromArgb(172,182,196), Blue=Color.FromArgb(42,153,255);
    public MainForm(AppData d) {
        data=d; Text="上班鼓励器 SV版"; BackColor=Bg; ForeColor=Color.White;
        AutoScaleMode=AutoScaleMode.None; FormBorderStyle=FormBorderStyle.None; MaximizeBox=false; StartPosition=FormStartPosition.CenterScreen;
        using(Graphics g=CreateGraphics()) scale=Math.Min(g.DpiX/96f,Math.Min((Screen.PrimaryScreen.WorkingArea.Height-40)/508f,(Screen.PrimaryScreen.WorkingArea.Width-40)/280f));
        ClientSize=new Size((int)(280*scale),(int)(508*scale));
        DoubleBuffered=true; TopMost=d.Settings.AlwaysOnTop;
        SetButton(settings,"设置",222,40,42,24,8); settings.Fill=Color.FromArgb(62,70,82);
        SetButton(pause,"暂停计时",16,202,248,30,9);
        SetButton(minimize,"－",228,3,22,22,8);minimize.Fill=Bg;minimize.AccessibleName="最小化";minimize.Click+=delegate{WindowState=FormWindowState.Minimized;};
        SetButton(close,"×",253,3,22,22,10);close.Fill=Bg;close.AccessibleName="关闭窗口";close.Click+=delegate{Close();};
        SetButton(overtime,"开始加班",170,421,94,25,8);overtime.Fill=Color.FromArgb(47,110,175);
        SetButton(overtimeType,"",16,421,144,25,8);overtimeType.Fill=Color.FromArgb(62,70,82);overtimeType.AccessibleName="加班类型";
        string[] types={"工作日 × 0.75","周末 × 1","法定节假日 × 1.5"};
        OvertimeRecord running=OvertimeEngine.Running(data);overtimeCategory=running!=null ? running.Category:Engine.IsWorkday(data.Settings,Engine.ShiftDay(data.Settings,DateTime.Now))?0:1;
        overtimeType.Text=types[overtimeCategory]+" ▾";
        overtimeMenu.BackColor=Color.FromArgb(62,70,82);overtimeMenu.ForeColor=Color.White;overtimeMenu.Font=overtimeType.Font;
        for(int i=0;i<types.Length;i++) {
            int selected=i;ToolStripMenuItem item=new ToolStripMenuItem(types[i]);overtimeMenu.Items.Add(item);
            item.Click+=delegate {if(OvertimeEngine.Running(data)!=null)return;overtimeCategory=selected;overtimeType.Text=types[selected]+" ▾";};
        }
        overtimeType.ContextMenuStrip=overtimeMenu;
        overtimeType.Click+=delegate {for(int i=0;i<types.Length;i++)((ToolStripMenuItem)overtimeMenu.Items[i]).Checked=i==overtimeCategory;overtimeMenu.Show(overtimeType,new Point(0,overtimeType.Height));};
        overtime.Click+=delegate {
            OvertimeRecord r=OvertimeEngine.Running(data);
            if(r==null) {
                r=OvertimeEngine.Start(data,overtimeCategory,DateTime.UtcNow,DateTime.Now);
                if(!TrySave())data.Overtime.Remove(r);
            }else {
                DateTime utc=DateTime.UtcNow;
                if(utc<r.StartUtc){MessageBox.Show(this,"当前时间早于加班开始时间，请检查电脑时间后再结束。","无法结束加班");return;}
                r.EndUtc=utc;if(!TrySave())r.EndUtc=DateTime.MinValue;
            }
            UpdateButton();Refresh();
        };
        settings.Click+=delegate { using(SettingsForm dialog=new SettingsForm(data.Settings)) if(dialog.ShowDialog(this)==DialogResult.OK) {
            Settings old=data.Settings; data.Settings=dialog.Result;
            if(TrySave()) TopMost=data.Settings.AlwaysOnTop; else data.Settings=old;
            Refresh();
        }};
        pause.Click+=delegate {
            PauseRecord p=Engine.OpenPause(data); DateTime old=DateTime.MinValue;
            if(p==null) { p=new PauseRecord {Start=DateTime.Now}; data.Pauses.Add(p); }
            else { old=p.End; p.End=DateTime.Now; }
            if(!TrySave()) { if(old==DateTime.MinValue && p.End!=DateTime.MinValue) p.End=old; else data.Pauses.Remove(p); }
            Refresh();
        };
        timer.Interval=250; timer.Tick+=delegate { UpdateButton(); Invalidate(); }; timer.Start(); UpdateButton();
        FormClosed+=delegate {timer.Dispose();overtimeMenu.Dispose();};
    }
    void SetButton(FlatButton b,string text,int x,int y,int w,int h,int fs) {b.Text=text;b.SetBounds((int)(x*scale),(int)(y*scale),(int)(w*scale),(int)(h*scale));b.Font=new Font("Microsoft YaHei UI",fs*scale*1.333f,FontStyle.Bold,GraphicsUnit.Pixel); Controls.Add(b);}
    bool TrySave() {try {Store.Save(data);return true;}catch(Exception e){MessageBox.Show(this,"无法保存，请把整个文件夹放到可写的位置。\n"+e.Message,"保存失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);return false;}}
    void UpdateButton() {
        pause.Text=Engine.OpenPause(data)==null ? "暂停计时":"继续计时";
        bool running=OvertimeEngine.Running(data)!=null;
        overtime.Text=running?"加班结束":"开始加班";overtimeType.Enabled=!running;
    }
    protected override void OnMouseDown(MouseEventArgs e) {base.OnMouseDown(e);if(e.Button==MouseButtons.Left && e.Y<28*scale){dragOffset=e.Location;Capture=true;}}
    protected override void OnMouseMove(MouseEventArgs e) {base.OnMouseMove(e);if(dragOffset.HasValue && Capture)Location=new Point(Cursor.Position.X-dragOffset.Value.X,Cursor.Position.Y-dragOffset.Value.Y);}
    protected override void OnMouseUp(MouseEventArgs e) {base.OnMouseUp(e);dragOffset=null;Capture=false;}
    protected override void OnMouseCaptureChanged(EventArgs e) {base.OnMouseCaptureChanged(e);if(!Capture)dragOffset=null;}
    public static GraphicsPath Round(RectangleF r,float radius) { GraphicsPath p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));if(d<=0)return p;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p; }
    void TextAt(Graphics g,string text,float x,float y,float width,float height,float size,Color color,FontStyle style) {
        using(Font f=new Font("Microsoft YaHei UI",size,style,GraphicsUnit.Pixel)) using(SolidBrush b=new SolidBrush(color)) using(StringFormat sf=new StringFormat {Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap}) g.DrawString(text,f,b,new RectangleF(x,y,width,height),sf);
    }
    void T(Graphics g,string t,float x,float y,float w,float h,float sz,Color c) {TextAt(g,t,x,y,w,h,sz,c,FontStyle.Regular);}
    void Box(Graphics g,float x,float y,float w,float h,Color c,float r) { using(GraphicsPath p=Round(new RectangleF(x,y,w,h),r)) using(SolidBrush b=new SolidBrush(c))g.FillPath(b,p); }
    static string Duration(TimeSpan t) {long sec=Math.Max(0,(long)Math.Ceiling(t.TotalSeconds));return (sec/3600)+"小时"+(sec/60%60)+"分"+(sec%60)+"秒";}
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); Graphics g=e.Graphics;g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        TextAt(g,"上班鼓励器",12,6,76,19,11,Color.White,FontStyle.Regular);TextAt(g,"SV版",88,6,50,19,11,Blue,FontStyle.Bold);
        using(Pen border=new Pen(Color.FromArgb(70,82,98)))g.DrawRectangle(border,.5f,.5f,279,507);
        using(Pen line=new Pen(Color.FromArgb(60,70,84)))g.DrawLine(line,1,28,279,28);
        g.TranslateTransform(0,28);
        Settings s=data.Settings;DateTime now=DateTime.Now,day=Engine.ShiftDay(s,now);bool paused=Engine.OpenPause(data)!=null;List<Span> blocks=Engine.Blocks(s,day);bool active=Engine.Active(s,day,now);
        Color amountColor=Color.FromArgb(s.AmountColorArgb);
        string status,detail;
        if(paused){status="已暂停";detail="正常计时已暂停，点击「继续计时」恢复";}
        else if(blocks.Count==0){status="今天休息";detail="好好休息，也是为了更好地出发";}
        else if(now<blocks[0].A){status="尚未上班";detail="距离上班还有 "+Duration(blocks[0].A-now);}
        else if(now>=blocks[blocks.Count-1].B){status="下班啦";detail="今天辛苦了，剩下的时间属于自己";}
        else {status=active ? "上班中":"午休中";detail="距离下班还有 "+Duration(blocks[blocks.Count-1].B-now);}
        Box(g,16,18,3,17,paused?Muted:Blue,1);TextAt(g,status,27,13,182,26,16,Color.White,FontStyle.Bold);T(g,detail,16,47,248,21,11,Muted);
        T(g,"今日已赚",16,79,248,21,11,Muted);
        DateTime utc=DateTime.UtcNow;
        decimal pay=Engine.Pay(data,day,now)+OvertimeEngine.Total(data,day,utc,false),month=Engine.Month(data,day,now)+OvertimeEngine.Total(data,day,utc,true);string money=pay.ToString("F4");float size=money.Length>11?22:money.Length>9?27:34;
        T(g,"¥",16,115,17,26,18,amountColor);TextAt(g,money,35,97,229,48,size,amountColor,FontStyle.Bold);
        decimal dailyPay=Engine.DailyPay(s,day);
        decimal speed=active&&!paused?dailyPay/(decimal)Engine.Planned(s):0;
        T(g,"正常速度",16,147,62,20,10,Muted);T(g,"¥"+speed.ToString("F4")+" / 秒",80,147,184,20,10,amountColor);
        double worked=Engine.Seconds(data,day,now),planned=Engine.Planned(s),progress=Math.Min(1,worked/planned);
        TextAt(g,"工时进度  "+progress.ToString("P1"),16,222,248,22,12,Color.White,FontStyle.Bold);
        T(g,"已工作 "+(worked/3600).ToString("F2")+" / "+(planned/3600).ToString("F2")+" 小时",16,245,248,21,10,Muted);
        Box(g,16,271,248,6,Color.FromArgb(73,82,96),3);if(progress>0)Box(g,16,271,(float)(248*progress),6,Blue,3);
        using(Pen p=new Pen(Color.FromArgb(72,81,94)))g.DrawLine(p,16,293,264,293);
        T(g,"本月累计 · 估算",16,308,115,21,11,Color.White);TextAt(g,"¥"+month.ToString("F2"),136,301,128,28,18,amountColor,FontStyle.Bold);
        T(g,"本月 "+Engine.Workdays(s,day)+" 个工作日 · 含加班估算",16,335,248,18,9,Muted);
        using(Pen line=new Pen(Color.FromArgb(72,81,94)))g.DrawLine(line,16,360,264,360);
        OvertimeRecord ot=OvertimeEngine.Running(data);
        bool isRunning=ot!=null;if(ot==null && data.Overtime.Count>0)ot=data.Overtime[data.Overtime.Count-1];
        TextAt(g,isRunning?"加班计时中":"加班计时",16,371,147,22,12,isRunning?Blue:Color.White,FontStyle.Bold);
        T(g,ot==null?"未开始":Duration(TimeSpan.FromSeconds(Math.Floor(OvertimeEngine.Seconds(ot,utc)))),16,425,248,20,10,Muted);
        decimal hours=ot==null?0:OvertimeEngine.Hours(ot,utc),otPay=ot==null?0:OvertimeEngine.Pay(ot,utc);
        T(g,"本次计薪 "+hours.ToString("0.0")+" h",16,450,122,22,11,Muted);TextAt(g,"¥"+otPay.ToString("F2"),144,445,120,27,17,amountColor,FontStyle.Bold);
    }
}
public class SettingsForm : Form {
    public Settings Result;
    NumericUpDown pay;
    Color selectedColor;
    List<DayOverride> calendar=new List<DayOverride>();
    DateTimePicker start,end,ls,le;CheckBox lunch,top;CheckBox[] days=new CheckBox[7];
    public SettingsForm(Settings s) {
        Text="工资与外观设置";Font=new Font("Microsoft YaHei UI",9);AutoScaleMode=AutoScaleMode.None;ClientSize=new Size(430,566);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(245,247,250);
        Label help=new Label {Text="日薪 = 月薪 ÷ 当月实际工作日，每月自动重算。",AutoSize=true,Location=new Point(22,18)};Controls.Add(help);
        pay=Number("月薪（元）",58,s.MonthlyPay,1,31000000);
        start=TimeField("上班时间",102,s.StartMinute);end=TimeField("下班时间",146,s.EndMinute);
        lunch=new CheckBox {Text="扣除午休（不计薪）",Checked=s.Lunch,Location=new Point(24,194),AutoSize=true};Controls.Add(lunch);
        ls=TimeField("午休开始",226,s.LunchStart);le=TimeField("午休结束",270,s.LunchEnd);
        Label dl=new Label {Text="工作日",Location=new Point(24,322),AutoSize=true};Controls.Add(dl);
        string[] names={"日","一","二","三","四","五","六"};
        for(int i=0;i<7;i++){days[i]=new CheckBox {Text=names[i],Checked=s.Days[i],Location=new Point(96+i*44,320),Size=new Size(44,25)};Controls.Add(days[i]);}
        foreach(DayOverride item in s.Calendar) calendar.Add(new DayOverride {Date=item.Date,Workday=item.Workday});
        Button schedule=new Button {Text="调整本月排班…",Location=new Point(24,354),Size=new Size(145,30)};Controls.Add(schedule);
        Label countLabel=new Label {Location=new Point(180,360),Size=new Size(220,24)};Controls.Add(countLabel);
        Action updateCount=delegate {Settings temp=new Settings {Calendar=calendar};for(int i=0;i<7;i++)temp.Days[i]=days[i].Checked;countLabel.Text=DateTime.Today.ToString("yyyy-MM")+"："+Engine.Workdays(temp,DateTime.Today)+" 个工作日";};
        foreach(CheckBox check in days)check.CheckedChanged+=delegate{updateCount();};updateCount();
        schedule.Click+=delegate {
            Settings temp=new Settings {Calendar=calendar};for(int i=0;i<7;i++)temp.Days[i]=days[i].Checked;
            using(CalendarForm dialog=new CalendarForm(temp,DateTime.Today)) if(dialog.ShowDialog(this)==DialogResult.OK){calendar=dialog.Result;updateCount();}
        };
        selectedColor=Color.FromArgb(s.AmountColorArgb);
        AddLabel("金额数字颜色",409);
        Button color=new Button {Text="选择颜色…",Location=new Point(180,403),Size=new Size(118,30)};Controls.Add(color);
        Panel swatch=new Panel {BackColor=selectedColor,Location=new Point(312,403),Size=new Size(88,30),BorderStyle=BorderStyle.FixedSingle,AccessibleName="当前金额颜色预览"};Controls.Add(swatch);
        color.Click+=delegate {using(ColorDialog picker=new ColorDialog {Color=selectedColor,FullOpen=true}) if(picker.ShowDialog(this)==DialogResult.OK){selectedColor=Color.FromArgb(255,picker.Color);swatch.BackColor=selectedColor;}};
        top=new CheckBox {Text="窗口始终置顶",Checked=s.AlwaysOnTop,Location=new Point(24,449),AutoSize=true};Controls.Add(top);
        Label note=new Label {Text="节假日、调休请在「调整本月排班」中勾选。\n修改月薪或排班会重新计算本月估算。",Location=new Point(24,478),Size=new Size(390,39),ForeColor=Color.DimGray};Controls.Add(note);
        Button ok=new Button {Text="保存设置",Location=new Point(282,524),Size=new Size(118,30)};Controls.Add(ok);AcceptButton=ok;
        Button cancel=new Button {Text="取消",DialogResult=DialogResult.Cancel,Location=new Point(184,524),Size=new Size(88,30)};Controls.Add(cancel);CancelButton=cancel;
        lunch.CheckedChanged+=delegate{ls.Enabled=le.Enabled=lunch.Checked;};ls.Enabled=le.Enabled=lunch.Checked;
        ok.Click+=delegate {
            Settings n=new Settings {MonthlyPay=pay.Value,Calendar=calendar,StartMinute=Minute(start),EndMinute=Minute(end),Lunch=lunch.Checked,LunchStart=Minute(ls),LunchEnd=Minute(le),AmountColorArgb=selectedColor.ToArgb(),AlwaysOnTop=top.Checked};
            for(int i=0;i<7;i++)n.Days[i]=days[i].Checked;
            if(!Engine.Validate(n)){MessageBox.Show(this,"请至少选择一个工作日；上下班时间不能相同；午休必须位于班次之内，且保留计薪时间。","请检查设置");return;}
            Result=n;DialogResult=DialogResult.OK;Close();
        };
        float factor;
        using(Graphics g=CreateGraphics()) factor=Math.Min(g.DpiX/96f*.92f,Math.Min((Screen.PrimaryScreen.WorkingArea.Height-65)/566f,(Screen.PrimaryScreen.WorkingArea.Width-40)/430f));
        foreach(Control c in Controls) c.Bounds=new Rectangle((int)(c.Left*factor),(int)(c.Top*factor),(int)(c.Width*factor),(int)(c.Height*factor));
        Font=new Font("Microsoft YaHei UI",11*factor,FontStyle.Regular,GraphicsUnit.Pixel);
        ClientSize=new Size((int)(430*factor),(int)(566*factor));
    }
    void AddLabel(string t,int y){Controls.Add(new Label {Text=t,Location=new Point(24,y),AutoSize=true});}
    NumericUpDown Number(string name,int y,decimal val,decimal min,decimal max){AddLabel(name,y+4);NumericUpDown n=new NumericUpDown {Location=new Point(180,y),Size=new Size(220,28),DecimalPlaces=2,Minimum=min,Maximum=max,Value=val,ThousandsSeparator=true};Controls.Add(n);return n;}
    DateTimePicker TimeField(string name,int y,int minute){AddLabel(name,y+4);DateTimePicker p=new DateTimePicker {Location=new Point(180,y),Size=new Size(220,28),Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Value=DateTime.Today.AddMinutes(minute)};Controls.Add(p);return p;}
    int Minute(DateTimePicker p){return p.Value.Hour*60+p.Value.Minute;}
}
public class CalendarForm : Form {
    public List<DayOverride> Result;
    CheckBox[] dates;
    public CalendarForm(Settings s, DateTime month) {
        month=new DateTime(month.Year,month.Month,1);
        Text=month.ToString("yyyy年MM月")+" · 实际排班";AutoScaleMode=AutoScaleMode.None;
        FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
        Controls.Add(new Label {Text="勾选为上班，取消为休息（含节假日、调休）。",Location=new Point(20,18),Size=new Size(350,25)});
        string[] names={"日","一","二","三","四","五","六"};
        for(int i=0;i<7;i++) Controls.Add(new Label {Text=names[i],Location=new Point(22+i*50,52),Size=new Size(45,22),TextAlign=ContentAlignment.MiddleCenter});
        dates=new CheckBox[DateTime.DaysInMonth(month.Year,month.Month)];
        for(int i=0;i<dates.Length;i++) {
            DateTime date=month.AddDays(i);int position=(int)month.DayOfWeek+i;
            dates[i]=new CheckBox {Text=(i+1).ToString(),Checked=Engine.IsWorkday(s,date),Location=new Point(25+position%7*50,82+position/7*33),Size=new Size(47,27),AccessibleName=date.ToString("yyyy-MM-dd")+" 是否上班"};Controls.Add(dates[i]);
        }
        Button reset=new Button {Text="按每周排班重置",Location=new Point(20,291),Size=new Size(155,30)};Controls.Add(reset);
        reset.Click+=delegate {for(int i=0;i<dates.Length;i++)dates[i].Checked=s.Days[(int)month.AddDays(i).DayOfWeek];};
        Button ok=new Button {Text="确定",Location=new Point(278,334),Size=new Size(90,30)};Controls.Add(ok);AcceptButton=ok;
        Button cancel=new Button {Text="取消",DialogResult=DialogResult.Cancel,Location=new Point(178,334),Size=new Size(90,30)};Controls.Add(cancel);CancelButton=cancel;
        ok.Click+=delegate {
            Result=new List<DayOverride>();
            foreach(DayOverride item in s.Calendar) if(item.Date.Year!=month.Year || item.Date.Month!=month.Month)Result.Add(new DayOverride {Date=item.Date,Workday=item.Workday});
            for(int i=0;i<dates.Length;i++) {DateTime date=month.AddDays(i);if(dates[i].Checked!=s.Days[(int)date.DayOfWeek])Result.Add(new DayOverride {Date=date,Workday=dates[i].Checked});}
            DialogResult=DialogResult.OK;Close();
        };
        float factor;using(Graphics g=CreateGraphics())factor=Math.Min(g.DpiX/96f*.92f,Math.Min((Screen.PrimaryScreen.WorkingArea.Height-65)/380f,(Screen.PrimaryScreen.WorkingArea.Width-40)/390f));
        foreach(Control c in Controls)c.Bounds=new Rectangle((int)(c.Left*factor),(int)(c.Top*factor),(int)(c.Width*factor),(int)(c.Height*factor));
        Font=new Font("Microsoft YaHei UI",11*factor,FontStyle.Regular,GraphicsUnit.Pixel);ClientSize=new Size((int)(390*factor),(int)(380*factor));
    }
}
public static class Program {
    [STAThread] public static void Main() {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        bool created;using(Mutex mutex=new Mutex(true,"Local\\WorkEncourager_Portable_V1",out created)) {
            if(!created){MessageBox.Show("上班鼓励器已经在运行，请查看任务栏。","上班鼓励器");return;}
            try {
                AppData data;
                try{data=Store.Load();}catch(Exception e){MessageBox.Show("配置无法读取，程序未修改原文件。\n请从 data.xml.bak 恢复，或将 data.xml 改名后重新打开。\n"+e.Message,"配置读取失败");return;}
                Application.Run(new MainForm(data));
            }catch(Exception e){MessageBox.Show(e.Message,"运行出错",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CampusNetHelper
{
    /// <summary>
    /// 测速实时波形图：下载青色看左轴，上传紫色看右轴。
    ///
    /// 为什么不直接用 Progress 里那个累计 Mbps：
    ///   累计 Mbps 是"从头到现在平均多少"，画出来是一条缓缓爬升的斜线，
    ///   看不出抖动、也看不出中途掉速。波形要的是**这一瞬**跑了多少，
    ///   所以由 SpeedTestEngine 的采样线程每 200ms 单独发点，这里只负责画。
    ///
    /// 为什么左右各一条纵轴：
    ///   校园网常见下载 600M / 上传 60M，差十倍。共用一条轴时上传那条会贴在地板上，
    ///   完全看不出起伏 —— 而"能不能看出起伏"正是画这张图的目的。
    ///   两条轴各配自己的一色（左青右紫），并且图例里写明看哪条轴，不会读错。
    ///
    /// 横轴是**固定时间窗**（30 秒），不是"把已有采样拉满整宽" ——
    ///   后者会让曲线在上传阶段开始时整体横向压缩，看着像在"呼吸"。
    ///   固定窗口下下载山丘和上传山丘首尾相接，时间关系一眼可读。
    ///
    /// 画法用 DrawingContext 直接描（不生成一堆 Polyline 控件）：
    ///   5 次/秒 的重绘如果每帧都重建几十个视觉对象，界面会明显发卡。
    /// </summary>
    public class SpeedChart : FrameworkElement
    {
        private const int Capacity = 150;      // 150 × 200ms = 30 秒
        private const double GutterL = 40;     // 左边留给下载刻度数字
        private const double GutterR = 44;     // 右边留给上传刻度数字
        private const double PadTop = 10;      // 顶部留一点，最上面那行刻度数字不至于顶到框线
        private const double PadBottom = 8;
        // 5 格 → 6 条刻度线。必须是 5 而不是 4：
        // 量程 150 四等分是 37.5，刻度数字会变成 38 / 113 这种没法读的怪数；
        // 五等分是 30，对齐得刚刚好（1000→200、200→40、300→60、10→2 也一样）。
        private const int GridCount = 5;

        /// <summary>-1 表示"这个时刻没在测这一项"（下载/上传是前后两个阶段，共用一条时间轴）。</summary>
        private const double Gap = -1.0;

        private readonly List<double> _dl = new List<double>();
        private readonly List<double> _ul = new List<double>();

        private double _dlScale = 10;
        private double _ulScale = 10;
        private bool _hasDl;
        private bool _hasUl;

        // 参考那种"下载青 / 上传紫"的配色，浅色深色主题下都够醒目。
        // public：窗口要拿同一对颜色画图例，颜色只在这一处定义，别两边各写一份走岔了。
        public static readonly Color DlColor = Color.FromRgb(0x2D, 0xD4, 0xBF);
        public static readonly Color UlColor = Color.FromRgb(0xA7, 0x8B, 0xFA);

        /// <summary>
        /// 自己申报尺寸。
        /// FrameworkElement 不重写 MeasureOverride 的话默认申报 0×0，
        /// 放进 StackPanel 里会被压成一条线 —— 波形根本不会出现。
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double h = Height;
            if (double.IsNaN(h)) h = 170;

            double w = availableSize.Width;
            if (double.IsNaN(w) || double.IsInfinity(w)) w = 0;
            if (w < 0) w = 0;

            return new Size(w, h);
        }

        public void Clear()
        {
            _dl.Clear();
            _ul.Clear();
            _dlScale = 10;
            _ulScale = 10;
            _hasDl = false;
            _hasUl = false;
            InvalidateVisual();
        }

        public void AddSample(bool isUpload, double mbps)
        {
            if (mbps < 0) mbps = 0;
            if (double.IsNaN(mbps) || double.IsInfinity(mbps)) mbps = 0;

            _dl.Add(isUpload ? Gap : mbps);
            _ul.Add(isUpload ? mbps : Gap);

            while (_dl.Count > Capacity)
            {
                _dl.RemoveAt(0);
                _ul.RemoveAt(0);
            }

            if (isUpload)
            {
                _hasUl = true;
                _ulScale = ScaleOf(_ul);
            }
            else
            {
                _hasDl = true;
                _dlScale = ScaleOf(_dl);
            }

            InvalidateVisual();
        }

        /// <summary>
        /// 量程按本阶段出现过的最大值定。
        ///
        /// 一度改成分位数（避开上传起步那个假尖峰），后来退回来了：
        /// 分位数在"峰值零星冒出"的场合会把真实的高点截平 ——
        /// 实测有一轮下载是"起头一阵、中间停滞、末尾又一阵"，
        /// 分位数只认中间那段停滞，结果两头的真实峰值全被压在顶格里看不出来。
        ///
        /// 真正的假尖峰（写缓冲区一次性冲刷）已经由采样端跳过第一拍解决了，
        /// 这里就该老老实实按最大值取，不截断任何数据。
        /// </summary>
        private static double ScaleOf(List<double> data)
        {
            double peak = 0;
            for (int i = 0; i < data.Count; i++)
            {
                if (data[i] > peak) peak = data[i];
            }
            return NiceScale(peak);
        }

        /// <summary>
        /// 纵轴上限取"整数好看"的刻度，再留 15% 余量。
        /// 不做这个的话上限会是 623.4 这种数，刻度数字全是无意义的小数。
        ///
        /// 档位取得比通常的 1/2/5 密一些（多了 1.5 / 2.5 / 3 / 4 / 6 / 8）：
        /// 档位太稀时，峰值 250 会被抬到 500，等于凭空丢掉一半分辨率。
        /// </summary>
        private static double NiceScale(double peak)
        {
            double target = peak * 1.15;
            if (target < 10) target = 10;

            double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double norm = target / mag;

            double[] steps = new double[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 };
            double step = steps[steps.Length - 1];
            for (int i = 0; i < steps.Length; i++)
            {
                if (norm <= steps[i]) { step = steps[i]; break; }
            }

            return step * mag;
        }

        /// <summary>把曲线色往文字色的方向兑一点，让刻度数字在白底/黑底上都读得清。</summary>
        private static Color Blend(Color c, Color toward, double t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromRgb(
                (byte)(c.R + (toward.R - c.R) * t),
                (byte)(c.G + (toward.G - c.G) * t),
                (byte)(c.B + (toward.B - c.B) * t));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 80 || h < 40) return;

            double plotX = GutterL;
            double plotW = w - GutterL - GutterR;
            double plotY = PadTop;
            double plotH = h - PadTop - PadBottom;
            if (plotW < 40 || plotH < 20) return;

            // FormattedText 必须知道当前缩放（PixelsPerDip），不传会警告 + 高分屏上字发虚。
            // 从 PresentationSource 拿 TransformToDevice.M11 就是它，比硬写 1.0 靠谱。
            double ppd = 1.0;
            PresentationSource ps = PresentationSource.FromVisual(this);
            if (ps != null && ps.CompositionTarget != null)
            {
                double m11 = ps.CompositionTarget.TransformToDevice.M11;
                if (m11 > 0.1 && m11 < 10) ppd = m11;
            }

            DrawGrid(dc, plotX, plotY, plotW, plotH, ppd);

            bool empty = !_hasDl && !_hasUl;
            if (empty)
            {
                DrawHint(dc, plotX, plotY, plotW, plotH, ppd);
                return;
            }

            // 只有下载时按左轴画，只有上传时按右轴画
            if (_hasUl)
            {
                int lastDl = -1;
                for (int i = _dl.Count - 1; i >= 0; i--)
                {
                    if (_dl[i] >= 0) { lastDl = i; break; }
                }
                if (lastDl >= 0) DrawDivider(dc, plotX, plotY, plotW, plotH, lastDl + 1);
            }

            if (_hasDl) DrawSeries(dc, _dl, DlColor, _dlScale, plotX, plotY, plotW, plotH);
            if (_hasUl) DrawSeries(dc, _ul, UlColor, _ulScale, plotX, plotY, plotW, plotH);
        }

        private void DrawGrid(DrawingContext dc, double plotX, double plotY, double plotW,
                              double plotH, double ppd)
        {
            Pen pen = new Pen(new SolidColorBrush(Theme.Divider), 1);
            Typeface face = new Typeface(new FontFamily(MainWindow.FontUi), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal);
            CultureInfo ci = CultureInfo.CurrentCulture;

            Brush dlBrush = new SolidColorBrush(Blend(DlColor, Theme.TextMuted, 0.35));
            Brush ulBrush = new SolidColorBrush(Blend(UlColor, Theme.TextMuted, 0.35));

            for (int i = 0; i <= GridCount; i++)
            {
                double frac = (double)i / GridCount;
                double y = plotY + plotH - plotH * frac;

                dc.DrawLine(pen, new Point(plotX, y), new Point(plotX + plotW, y));

                // 左侧：下载那一套刻度；右侧：上传那一套
                if (_hasDl)
                {
                    var lt = new FormattedText((_dlScale * frac).ToString("0"), ci,
                        FlowDirection.LeftToRight, face, 10, dlBrush, ppd);
                    dc.DrawText(lt, new Point(plotX - 6 - lt.Width, y - lt.Height / 2));
                }

                if (_hasUl)
                {
                    var rt = new FormattedText((_ulScale * frac).ToString("0"), ci,
                        FlowDirection.LeftToRight, face, 10, ulBrush, ppd);
                    dc.DrawText(rt, new Point(plotX + plotW + 6, y - rt.Height / 2));
                }
            }

            // 这里【不写"Mbps"单位】—— 顶部那条刻度线就在 plotY 上，
            // 再往上塞一行单位文字，正好把最大的那个刻度数字（1000 / 200）盖掉。
            // 单位统一由窗口图例那一行说明。
        }

        private void DrawHint(DrawingContext dc, double plotX, double plotY, double plotW,
                              double plotH, double ppd)
        {
            Typeface face = new Typeface(new FontFamily(MainWindow.FontUi), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText("点「开始测速」后，这里会实时画出网速曲线",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12,
                new SolidColorBrush(Theme.TextFaint), ppd);

            dc.DrawText(ft, new Point(plotX + (plotW - ft.Width) / 2,
                                       plotY + (plotH - ft.Height) / 2));
        }

        /// <summary>下载/上传交界处画一条浅虚线，提示"刻度从这里换了一套"。</summary>
        private void DrawDivider(DrawingContext dc, double plotX, double plotY, double plotW,
                                 double plotH, int index)
        {
            if (index < 1) return;
            if (index > Capacity - 1) index = Capacity - 1;

            double x = plotX + (double)index / (Capacity - 1) * plotW;

            Pen pen = new Pen(new SolidColorBrush(Theme.Divider), 1);
            pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
            dc.DrawLine(pen, new Point(x, plotY), new Point(x, plotY + plotH));
        }

        /// <summary>画一条曲线：填充渐变面积 + 描线。遇到 -1（该阶段没测）就断开。</summary>
        private void DrawSeries(DrawingContext dc, List<double> data, Color color, double scale,
                                double plotX, double plotY, double plotW, double plotH)
        {
            int n = data.Count;
            if (n == 0 || scale <= 0) return;

            double baseline = plotY + plotH;

            // 先按连续段切分
            int i = 0;
            while (i < n)
            {
                if (data[i] < 0) { i++; continue; }

                var pts = new List<Point>();
                while (i < n && data[i] >= 0)
                {
                    double x = plotX + (double)i / (Capacity - 1) * plotW;
                    double y = plotY + plotH - (data[i] / scale) * plotH;
                    if (y < plotY) y = plotY;
                    if (y > baseline) y = baseline;
                    pts.Add(new Point(x, y));
                    i++;
                }

                if (pts.Count == 1)
                {
                    // 只有一个点时画个小圆点，不然刚起步的两百毫秒是空白
                    dc.DrawEllipse(new SolidColorBrush(color), null, pts[0], 2.5, 2.5);
                    continue;
                }

                // 面积
                var area = new PathGeometry();
                var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
                fig.Segments.Add(new PolyLineSegment(pts, true));

                var closing = new PolyLineSegment();
                closing.Points.Add(new Point(pts[pts.Count - 1].X, baseline));
                closing.Points.Add(new Point(pts[0].X, baseline));
                fig.Segments.Add(closing);
                area.Figures.Add(fig);

                var grad = new LinearGradientBrush(
                    Color.FromArgb(0xCC, color.R, color.G, color.B),
                    Color.FromArgb(0x10, color.R, color.G, color.B),
                    new Point(0, 0), new Point(0, 1));
                dc.DrawGeometry(grad, null, area);

                // 描线
                var line = new PathGeometry();
                var lfig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
                lfig.Segments.Add(new PolyLineSegment(pts, true));
                line.Figures.Add(lfig);

                dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                }, line);

                // 段尾最后一点标个小圆，看得出"最新在哪"
                Point last = pts[pts.Count - 1];
                dc.DrawEllipse(new SolidColorBrush(color), null, last, 3, 3);
            }
        }
    }
}

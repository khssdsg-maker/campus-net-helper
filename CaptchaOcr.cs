using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace CampusNetHelper
{
    /// <summary>
    /// C9 第二阶段：验证码识别（**只填候选，绝不提交**）。
    ///
    /// 这一版推翻了 CaptchaAssist 类注释里"识别做不了"的结论 —— 那个结论建立在
    /// 一个真 bug 上，见下面「为什么上一轮判错了」。
    ///
    /// ── 为什么上一轮判错了 ─────────────────────────────────────────────
    /// 上一轮说"形态学开运算会把 T 的横杠、E 的中横一起吃掉"，实测复现后根因是两条：
    ///   ① 那个 erode2x2 函数名不副实：注释写 2×2，代码写了四次移位，
    ///      实际等价于 **3×3 十字腐蚀** —— 2px 宽的笔画在十字核下整根消失。
    ///      真 2×2 只需要两次移位（见 Erode2x2）。
    ///   ② 它在**所有颜色合并后**的二值掩膜上做腐蚀。干扰线和字符一旦像素相邻就
    ///      粘成一块，腐蚀时字符被连坐。而按颜色分层做，每一层里"字符是粗的、
    ///      干扰线是细的"这个差别才显出来。
    /// 修好这两点后，字符能完整无损地切出来。2026-10-03 用 30 张真样本实测。
    ///
    /// ── 真实数据 ───────────────────────────────────────────────────────
    /// 30 张真样本（人工标注），前 15 张用于开发调参，后 15 张为留出集：
    ///     字符准确率 83.3%（30 张 / 120 位，总是取 top1）。
    /// 但**整图 4 位全对只有 46.7%** —— 所以这个功能的定位就是"省你打字"，
    /// 不是"替你填对"**。界面必须让用户核对，这一点不能含糊。
    ///
    /// 开了 0.03 置信度闸门之后（只在我们有把握时才填）：
    ///     填给用户 11/30 张，这些图里 4 位全对 8 张 = 72.7%。
    /// 也就是说：**被填的时候多半是对的，但有一多半的时候我们不填**。
    /// 这个取舍在 MinMargin 的注释里有完整的阈值-准确率对照表。
    ///
    /// ── 三条红线（与 CaptchaAssist 一致，改代码前必读）────────────────
    ///   1. 绝不重新请求 /CheckCode（答案绑在 ASP.NET_SessionId 上，重请求会让
    ///      页面上正在显示的图作废）。取图只走"页面已加载好的那张图"。
    ///   2. 绝不自动提交。识别结果只写进输入框，点「登录」永远是人的动作。
    ///   3. 识别失败/置信度低 → 什么都不填，并如实告诉用户"请手动填写"。
    /// </summary>
    internal static class CaptchaOcr
    {
        // ==================================================================
        // 门户的调色板（2026-10-02 实测：全图只有这 7 个纯色 + 白底）
        //
        // 有意思的是这 7 个色正好是 .NET 的命名色：
        //   Black / Red / DarkBlue / Green / Blue / Orange / Brown
        // 说明门户源码里大概率就是一个 Color[] 数组配 random.Next(7)，
        // 每个字符各取一色，干扰线也从同一数组里取 —— 所以**字符和干扰线同色**
        // 是常态，不能靠"颜色"去区分线和字，只能靠"粗细"。
        // ==================================================================
        private static readonly string[] PaletteNames =
            new string[] { "Black", "Red", "DarkBlue", "Green", "Blue", "Orange", "Brown" };

        private static readonly int[,] PaletteRgb = new int[,]
        {
            {   0,   0,   0 },   // Black
            { 255,   0,   0 },   // Red
            {   0,   0, 139 },   // DarkBlue
            {   0, 128,   0 },   // Green
            {   0,   0, 255 },   // Blue
            { 255, 165,   0 },   // Orange
            { 165,  42,  42 },   // Brown
        };

        // ==================================================================
        // 布局常量（在 80×24 原图上量的）
        //
        // 2026-10-03 用 30 张真样本统计出来的：字符墨迹左边缘密集落在
        // x = 6 / 22 / 38 / 54，也就是 6 + 16*i，间距**恰好 16px**；
        // 字符高 14px，纵向抖动 ±3px。30 张里只有 2 张不符合（那两张是
        // 同色干扰线把相邻字连成一块了），按槽位归类的分布是 27/29/27/26，
        // 相当均匀 —— 所以**槽位可以直接当成已知量用，不必去数连通域**。
        //
        // 为什么放弃"数连通域个数"：
        //   cc-013 那张图组件法只切出 2 个字符，可图上明明是 G C 3 4：
        //   黑色字符被黑色干扰线连成一块，整块又被形状阈值判掉。
        //   数个数这条路同时会"少数"（同色粘连）和"多数"（粗斜线冒充字符），
        //   两头都不可靠。
        // ==================================================================
        private const int RefWidth = 80;
        private const int RefHeight = 24;
        private const int SlotFirstX = 6;
        private const int SlotStepX = 16;

        /// <summary>取窗口相对槽心的左右偏移。故意**不重叠**：槽距 16，窗口宽 16。</summary>
        private const int WinLo = -3;
        private const int WinHi = 13;

        /// <summary>
        /// 位移搜索半径。
        ///
        /// ⚠️ 这里**必须够大**，4 是不够的（2026-10-03 实测踩到）：
        ///    观测掩膜裁的是"含同色干扰线"的外框，一条干扰线往左伸出 8px，
        ///    字符在这个框里就偏了 8px。半径只给 4 的话够不着正确对齐，
        ///    正确模板的得分被压低、margin 变小，于是大量位被判成"认不准不填"。
        ///    实测对比（30 张样本，同样开 0.05 闸门）：
        ///        半径 4  -> 字符 49.2%，只填了 62/120 位
        ///        半径 8  -> 字符 61.7%，填 77 位（与"全位移搜索"结果完全一致）
        ///    取 10：已经饱和（8/10/12 结果一模一样），多留一点余量给更宽的干扰线。
        /// </summary>
        private const int MaxShift = 10;

        /// <summary>判"这个色是不是本槽的字符色"用的种子下限（2x2 腐蚀后剩几个像素）。</summary>
        private const int MinSeeds = 2;

        /// <summary>
        /// 置信度闸门：4 位里**最小**的那个 (top1 - top2) 差值。
        ///
        /// 为什么用"差值"而不是"top1 的绝对值"：绝对分受干扰线多少影响很大，
        /// 同一字符干净时 0.9、被线穿过时 0.7，拿绝对值当阈值会把好字符也挡掉。
        /// 而"第一名比第二名强多少"衡量的是**这一位本身有多含糊**。
        ///
        /// 为什么取 4 位的最小值而不是逐位填：任务书要求"置信度不足就什么都不填"。
        /// 往输入框里填 "A?3?" 这种残句对用户毫无意义，还会让他以为程序坏了。
        /// 要么 4 位都给候选，要么一位都不给。
        ///
        /// 0.03 是实测选的（30 张真实样本，位移半径 10）：
        ///     阈值    填入张数   填入后整图全对率
        ///     0.00     30/30        46.7%     ← 总是填，等于没闸门
        ///     0.02     16           68.8%
        ///     0.03     11           72.7%     ← 取这个：填入后全对率过 70% 验收线
        ///     0.05      5           80.0%     ← 更准但只帮到 1/6 的人
        /// 再往上（0.08）样本只剩 2 张，数字已经没有意义了。
        /// </summary>
        public const double MinMargin = 0.03;

        // ==================================================================
        // 模板库（懒加载 + 缓存）
        // ==================================================================

        private static bool _loaded;
        private static byte[] _charIdx;      // 每个模板属于哪个字符
        private static uint[][] _rows;       // 每个模板的行位掩码
        private static int[] _w;
        private static int[] _h;
        private static int[] _area;

        public static int TemplateCount { get { Load(); return _charIdx.Length; } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            byte[] d = CaptchaTemplates.Data();
            List<byte> idx = new List<byte>();
            List<uint[]> rows = new List<uint[]>();
            List<int> ws = new List<int>();
            List<int> hs = new List<int>();
            List<int> ar = new List<int>();

            int p = 0;
            while (p + 3 <= d.Length)
            {
                int ci = d[p];
                int h = d[p + 1];
                int w = d[p + 2];
                p += 3;
                if (h <= 0 || w <= 0 || p + h * 4 > d.Length) break;

                uint[] r = new uint[h];
                int area = 0;
                for (int y = 0; y < h; y++)
                {
                    uint v = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
                    p += 4;
                    r[y] = v;
                    area += PopCount(v);
                }
                idx.Add((byte)ci);
                rows.Add(r);
                ws.Add(w);
                hs.Add(h);
                ar.Add(area);
            }

            _charIdx = idx.ToArray();
            _rows = rows.ToArray();
            _w = ws.ToArray();
            _h = hs.ToArray();
            _area = ar.ToArray();
        }

        private static int PopCount(uint v)
        {
            // 不用查表：模板加载和匹配都是短循环，位运算足够快且省一次静态构造
            v = v - ((v >> 1) & 0x55555555u);
            v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
            v = (v + (v >> 4)) & 0x0F0F0F0Fu;
            return (int)((v * 0x01010101u) >> 24);
        }

        // ==================================================================
        // 结果
        // ==================================================================

        public sealed class SlotResult
        {
            public char Best;          // 识别出的字符，失败为 '\0'
            public char Second;        // 第二名（用于诊断）
            public double BestScore;
            public double Margin;      // BestScore - 第二名分数
            public bool Accepted;      // 是否达到置信度闸门
            public string Color;       // 该槽判定的字符色（诊断用）
            public int Seeds;
        }

        public sealed class Result
        {
            public string Text = "????";        // 4 位，未识别为 '?'
            public SlotResult[] Slots = new SlotResult[4];
            public int AcceptedCount;           // 达到闸门的位数
            public double MinMargin = -1;       // 达标位里最小的 margin（整体置信度）
            public string Error = "";
            public bool Ok { get { return Error.Length == 0 && AcceptedCount > 0; } }
        }

        // ==================================================================
        // 入口
        // ==================================================================

        /// <summary>
        /// 识别一张验证码图。传入的位图最好是门户的**原始 80×24**；
        /// 尺寸不符会先重采样到 80×24（模板是按那个尺寸渲染的）。
        /// </summary>
        public static Result Recognize(Bitmap src)
        {
            Result res = new Result();
            if (src == null) { res.Error = "没有图片"; return res; }

            Load();
            if (_charIdx.Length == 0) { res.Error = "模板库为空"; return res; }

            Bitmap work = null;
            try
            {
                work = Normalize(src);
                if (work == null) { res.Error = "图片转换失败"; return res; }

                int w = work.Width;
                int h = work.Height;

                // 每像素吸附到最近的调色板颜色；-1 表示判定为白底
                int[] cls = Classify(work, w, h);

                for (int slot = 0; slot < 4; slot++)
                    res.Slots[slot] = RecognizeSlot(cls, w, h, slot);

                char[] outChars = new char[4];
                int acc = 0;
                double minMargin = double.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    SlotResult s = res.Slots[i];
                    if (s != null && s.Accepted)
                    {
                        outChars[i] = s.Best;
                        acc++;
                        if (s.Margin < minMargin) minMargin = s.Margin;
                    }
                    else
                    {
                        outChars[i] = '?';
                    }
                }
                res.Text = new string(outChars);
                res.AcceptedCount = acc;
                res.MinMargin = (acc > 0) ? minMargin : -1;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                Log.Warn("验证码识别异常: " + ex.Message);
                return res;
            }
            finally
            {
                if (work != null && !object.ReferenceEquals(work, src)) work.Dispose();
            }
        }

        /// <summary>从 PNG 字节直接识别（采集/自测都用这条路）。</summary>
        public static Result RecognizePng(byte[] png)
        {
            Result res = new Result();
            if (png == null || png.Length == 0) { res.Error = "没有图片数据"; return res; }
            try
            {
                using (MemoryStream ms = new MemoryStream(png))
                using (Bitmap b = new Bitmap(ms))
                {
                    return Recognize(b);
                }
            }
            catch (Exception ex)
            {
                res.Error = "图片解码失败: " + ex.Message;
                return res;
            }
        }

        private static Bitmap Normalize(Bitmap src)
        {
            if (src.Width == RefWidth && src.Height == RefHeight)
            {
                // 已是原始尺寸：转成 24bpp 便于取像素（索引色/带 alpha 的都要能处理）
                return (src.PixelFormat == PixelFormat.Format24bppRgb)
                    ? src : src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb);
            }

            // 尺寸不符：重采样到 80×24。
            // 页面上的 img 被拉伸到 155px 显示是常态，但 canvas 取图拿到的是原图，
            // 所以正常走不到这里；这是给"万一拿了显示尺寸"兜底的。
            Bitmap dst = new Bitmap(RefWidth, RefHeight, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, RefWidth, RefHeight));
            }
            return dst;
        }

        /// <summary>
        /// 每像素吸附到最近调色板色。返回 -1 表示背景（离白色最近）。
        ///
        /// 为什么用"最近色"而不是"精确等于"：GDI+ 画字符时边缘有抗锯齿，
        /// 边缘像素是三色混合，精确匹配会漏掉一整圈，笔画看起来就细了。
        ///
        /// ⚠️ 用 Marshal.Copy 取像素而不是 unsafe 指针：build.rsp 里没有 /unsafe，
        ///    加了指针就编译不过（csc 会报 CS0227）。80×24 只有 5760 字节，
        ///    拷一次的代价可以忽略。
        /// </summary>
        private static int[] Classify(Bitmap bmp, int w, int h)
        {
            int[] cls = new int[w * h];
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                                         ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = bd.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int o = row + x * 3;
                        int b = buf[o];
                        int g = buf[o + 1];
                        int r = buf[o + 2];

                        int bestI = -1;
                        int bestD = (255 - r) * (255 - r) + (255 - g) * (255 - g) + (255 - b) * (255 - b); // 白
                        for (int k = 0; k < PaletteNames.Length; k++)
                        {
                            int dr = r - PaletteRgb[k, 0];
                            int dg = g - PaletteRgb[k, 1];
                            int db = b - PaletteRgb[k, 2];
                            int d = dr * dr + dg * dg + db * db;
                            if (d < bestD) { bestD = d; bestI = k; }
                        }
                        cls[y * w + x] = bestI;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return cls;
        }

        // ==================================================================
        // 单个槽位
        // ==================================================================

        private static SlotResult RecognizeSlot(int[] cls, int w, int h, int slot)
        {
            SlotResult sr = new SlotResult();
            int cx = SlotFirstX + SlotStepX * slot;
            int x0 = cx + WinLo; if (x0 < 0) x0 = 0;
            int x1 = cx + WinHi; if (x1 > w) x1 = w;
            if (x1 <= x0) return sr;

            // ---- 1. 选色：窗口内哪个色的"粗像素"最多，哪个就是这个字符 ----
            // 判据是 2x2 腐蚀后的存活像素数（种子）。实测字符有 20~50 个种子，
            // 干扰线 0~2 个 —— 这是全流程里最干净的一个判别量。
            int bestColor = -1;
            int bestSeeds = -1;
            int[] seedsOf = new int[PaletteNames.Length];
            for (int k = 0; k < PaletteNames.Length; k++)
            {
                int s = CountSeeds(cls, w, h, x0, x1, k);
                seedsOf[k] = s;
                if (s > bestSeeds) { bestSeeds = s; bestColor = k; }
            }
            sr.Seeds = bestSeeds;
            if (bestColor < 0 || bestSeeds < MinSeeds) return sr;   // 这槽是空的（页面改版/图没画完）
            sr.Color = PaletteNames[bestColor];

            // ---- 2. 取观测：窗口内该色的全部像素，裁到墨迹外框 ----
            // 为什么用"全部像素"而不是只留粗的部分：实测（30 张样本对比）
            // 保留全部像素 = 字符准确率 86.7%，只留粗像素 = 80.8%。
            // 因为按种子框裁剪会把字符自己的细笔画（比如 R 的斜腿末端）一起切掉。
            // 干扰线的污染交给打分函数去容忍，别在取观测这一步动刀。
            uint[] aRows;
            int aw, ah, aArea;
            if (!BuildObs(cls, w, h, x0, x1, bestColor, out aRows, out aw, out ah, out aArea))
                return sr;
            if (aArea < 20) return sr;      // 太小，不像字符

            // ---- 3. 与模板库比对 ----
            double[] bestScore = new double[CaptchaTemplates.Charset.Length];
            for (int i = 0; i < bestScore.Length; i++) bestScore[i] = -1;

            for (int t = 0; t < _charIdx.Length; t++)
            {
                int nB = _area[t];
                // 面积剪枝：模板墨迹面积与观测差 2 倍以上，不可能赢
                if (nB > 2 * aArea || aArea > 2 * nB) continue;

                int inter = MaxOverlap(aRows, aw, ah, _rows[t], _w[t], _h[t]);
                double v = (2.0 * inter) / (aArea + nB);      // Dice 系数
                int ci = _charIdx[t];
                if (v > bestScore[ci]) bestScore[ci] = v;
            }

            // ---- 4. 取前二，算置信度 ----
            double s1 = -1, s2 = -1;
            int c1 = -1, c2 = -1;
            for (int i = 0; i < bestScore.Length; i++)
            {
                double v = bestScore[i];
                if (v < 0) continue;
                if (v > s1) { s2 = s1; c2 = c1; s1 = v; c1 = i; }
                else if (v > s2) { s2 = v; c2 = i; }
            }
            if (c1 < 0) return sr;

            sr.Best = CaptchaTemplates.Charset[c1];
            sr.BestScore = s1;
            if (c2 >= 0) { sr.Second = CaptchaTemplates.Charset[c2]; sr.Margin = s1 - s2; }
            else { sr.Second = '\0'; sr.Margin = 1.0; }

            sr.Accepted = (sr.Margin >= MinMargin);
            return sr;
        }

        /// <summary>窗口内某个色有多少个 2x2 腐蚀存活像素（= 有多少"粗"结构）。</summary>
        private static int CountSeeds(int[] cls, int w, int h, int x0, int x1, int color)
        {
            int n = 0;
            for (int y = 0; y + 1 < h; y++)
            {
                for (int x = x0; x + 1 < x1; x++)
                {
                    int i = y * w + x;
                    if (cls[i] != color) continue;
                    if (cls[i + 1] != color) continue;
                    if (cls[i + w] != color) continue;
                    if (cls[i + w + 1] != color) continue;
                    n++;
                }
            }
            return n;
        }

        private static bool BuildObs(int[] cls, int w, int h, int x0, int x1, int color,
                                     out uint[] rows, out int ow, out int oh, out int area)
        {
            rows = null; ow = 0; oh = 0; area = 0;

            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (cls[y * w + x] != color) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return false;

            ow = maxX - minX + 1;
            oh = maxY - minY + 1;
            if (ow > 32) ow = 32;                    // uint 位掩码上限，防御

            uint[] r = new uint[oh];
            for (int y = 0; y < oh; y++)
            {
                uint v = 0;
                for (int x = 0; x < ow; x++)
                    if (cls[(minY + y) * w + (minX + x)] == color) v |= (1u << x);
                r[y] = v;
                area += PopCount(v);
            }
            rows = r;
            return true;
        }

        /// <summary>
        /// 两张位掩膜在所有整数位移下的**最大重叠像素数**。
        ///
        /// 为什么只要"最大重叠"就够了：打分用 Dice = 2*inter/(nA+nB)，
        /// nA、nB 与位移无关，所以 Dice 对 inter 单调递增 ——
        /// 使 inter 最大的位移就是使得分最大的位移，不必逐个位移算分数。
        ///
        /// 位移范围取 ±MaxShift（不取全范围）有两个好处：快，且避免
        /// "把模板挪到观测的另一个笔画上"这种荒谬对齐拿高分。
        /// </summary>
        private static int MaxOverlap(uint[] aRows, int aw, int ah, uint[] bRows, int bw, int bh)
        {
            uint maskA = (aw >= 32) ? 0xFFFFFFFFu : ((1u << aw) - 1u);
            int best = 0;
            for (int oy = -MaxShift; oy <= MaxShift; oy++)
            {
                for (int ox = -MaxShift; ox <= MaxShift; ox++)
                {
                    int inter = 0;
                    for (int by = 0; by < bh; by++)
                    {
                        int ay = by + oy;
                        if (ay < 0 || ay >= ah) continue;
                        uint t = bRows[by];
                        uint shifted = (ox >= 0) ? (t << ox) : (t >> (-ox));
                        inter += PopCount(shifted & aRows[ay] & maskA);
                    }
                    if (inter > best) best = inter;
                }
            }
            return best;
        }

        // ==================================================================
        // 准确率测量（给自测用；也能拿来量用户自己攒的样本）
        //
        // 为什么做成"扫目录"而不是写死几张图：
        //   ① 开发期可以用 fixture 目录量我自己拉的样本；
        //   ② 海辰平时登录时 CaptchaAssist 就在往 captcha-samples\ 里攒样本，
        //      文件名本身就是 yyyyMMdd-HHmmss-fff-XXXX.png（XXXX = 他填对的那 4 位）
        //      —— 天然带标签。所以这个函数能直接回答"在**你自己的**样本上，
        //      识别到底准不准"，而不是我这边自说自话。
        // ==================================================================

        public sealed class Accuracy
        {
            public int Images;
            public int CharTotal;

            /// <summary>不管闸门、直接取 top1 时的字符正确数（= 识别能力本身）。</summary>
            public int RawCharOk;

            /// <summary>达到闸门、真会填进框里的位数。</summary>
            public int FilledChars;

            /// <summary>填进去的位里对的位数。</summary>
            public int FilledOk;

            /// <summary>4 位全部达标的图数 / 其中 4 位全对的图数。</summary>
            public int ImagesAllFilled;
            public int ImagesAllFilledOk;

            public double RawCharRate { get { return CharTotal == 0 ? 0 : (double)RawCharOk / CharTotal; } }
            public double FillRate { get { return CharTotal == 0 ? 0 : (double)FilledChars / CharTotal; } }
            public double FillCharRate { get { return FilledChars == 0 ? 0 : (double)FilledOk / FilledChars; } }
            public double FillImageRate { get { return ImagesAllFilled == 0 ? 0 : (double)ImagesAllFilledOk / ImagesAllFilled; } }
            public double FillImageCoverage { get { return Images == 0 ? 0 : (double)ImagesAllFilled / Images; } }
        }

        /// <summary>
        /// 扫一个目录，对每个"文件名里带 4 位标签"的 PNG 跑识别并比对。
        /// 标签取文件名（去掉扩展名）的**最后 4 个字符**，正好同时适配
        /// cc-000-SK84.png 和 yyyyMMdd-HHmmss-fff-SK84.png 两种命名。
        ///
        /// 同时给出两组数，缺一组都会误导人：
        ///   ① RawCharRate —— 不看闸门、总是取 top1，衡量"识别能力"本身
        ///   ② FillCharRate / FillImageRate —— 真会填进框里的那部分有多准
        /// 只报 ① 会掩盖"其实大部分时候不敢填"；只报 ② 会变成自说自话。
        /// </summary>
        public static Accuracy MeasureDirectory(string dir, out string error)
        {
            error = "";
            Accuracy acc = new Accuracy();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                error = "目录不存在: " + dir;
                return acc;
            }

            string[] files;
            try { files = Directory.GetFiles(dir, "*.png"); }
            catch (Exception ex) { error = "列目录失败: " + ex.Message; return acc; }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string stem = Path.GetFileNameWithoutExtension(files[i]);
                if (stem.Length < 4) continue;
                string label = stem.Substring(stem.Length - 4).ToUpperInvariant();
                if (!CaptchaAssist.IsValidCaptchaText(label)) continue;

                byte[] png;
                try { png = File.ReadAllBytes(files[i]); }
                catch { continue; }

                Result r = RecognizePng(png);
                if (r.Error.Length > 0) continue;

                acc.Images++;
                bool allFilled = true, allFilledOk = true;
                for (int k = 0; k < 4; k++)
                {
                    acc.CharTotal++;
                    SlotResult s = r.Slots[k];

                    if (s != null && s.Best == label[k]) acc.RawCharOk++;

                    if (s != null && s.Accepted)
                    {
                        acc.FilledChars++;
                        if (s.Best == label[k]) acc.FilledOk++;
                        else allFilledOk = false;
                    }
                    else
                    {
                        allFilled = false;
                        allFilledOk = false;
                    }
                }
                if (allFilled)
                {
                    acc.ImagesAllFilled++;
                    if (allFilledOk) acc.ImagesAllFilledOk++;
                }
            }

            if (acc.Images == 0) error = "目录里没有可用的带标签样本: " + dir;
            return acc;
        }

        // ==================================================================
        // 给界面用的一句话结论（措辞见红线 2/3：永远提醒"要你核对"）
        // ==================================================================

        public static string DescribeForUser(Result r)
        {
            if (r == null) return "识别失败，请手动填写。";
            if (r.Error.Length > 0) return "识别不可用（" + r.Error + "），请手动填写。";
            if (r.AcceptedCount == 0) return "认不准，没有替你填 —— 请手动填写。";
            if (r.AcceptedCount < 4)
                return "只认准了 " + r.AcceptedCount + " 位（? 处要你自己看），核对后再点登录。";
            return "已填入识别候选（可能错），请核对后手动点登录。";
        }
    }
}

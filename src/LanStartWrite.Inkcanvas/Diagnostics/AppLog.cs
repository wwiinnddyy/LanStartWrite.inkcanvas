using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LanStartWrite.Inkcanvas.Diagnostics;

/// <summary>
/// 一份**追加写、按大小封顶**的诊断日志，落在
/// <c>%LOCALAPPDATA%\LanStartWrite\logs\app-&lt;yyyyMMdd&gt;.log</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：用户报的三条问题（两块画布时墨迹卡、图片画布落笔错位、
/// 二级窗口先出空壳）**共同的特点是"只有真设备上才出现"** ——
/// 合成探针量到的每事件耗时不到半毫秒，复现不出卡顿；而"落笔位置偏了"与
/// "窗口先出边框"又都发生在真实指针 / 真实布局节奏上。
/// 没有日志时能做的只有猜，而这三条的猜法都指向完全不同的文件。
/// </para>
/// <para>
/// <b>三条例外，日志一律吞掉不抛</b>：写盘失败（磁盘满 / 目录被策略挡住）、
/// 被调用的代码顺手抛了别的异常、以及调用方忘了包 try。
/// <b>诊断设施不能成为新的故障源</b>——它跑在被诊断的路径上，
/// 而这些路径正是出问题时最要紧的那几条。
/// </para>
/// <para>
/// <b>封顶按天轮转 + 单文件封顶</b>：不做封顶的话，用户攒几周就能把日志写成
/// 磁盘占用问题，而那正是"电脑变慢"的另一个常见来源 —— 诊断手段变成病因。
/// </para>
/// </remarks>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string? _day;
    private const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>本次进程里是否成功打开过日志文件（诊断用，不给用户看）。</summary>
    internal static bool Enabled { get; private set; }

    internal static string FilePath => _path ?? "(未启用)";

    internal static void Write(string area, string message)
    {
        Write(area, message, null);
    }

    /// <summary>记一条带异常的消息。异常只取类型与消息，不取整条栈 —— 栈很长，而这三类问题的栈通常同一个样。</summary>
    internal static void Write(string area, string message, Exception? error)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message}");
            if (error is not null)
            {
                line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line} | {error.GetType().Name}: {error.Message}");
            }

            lock (Gate)
            {
                Append(line);
            }
        }
        catch
        {
            // 见类型注释：日志失败一律不抛。
        }
    }

    /// <summary>
    /// 量一段耗时并落一条。返回的 <see cref="IDisposable"/> 在释放时写出
    /// "区域 事件 耗时Nms"，超过 <paramref name="warnMs"/> 时带 <c>!</c> 标记。
    /// </summary>
    /// <remarks>
    /// <b>别拿它当基准测试。</b>它量的是"这段代码在这台机器此刻有多慢"，
    /// 而同一个指标在本机四轮实测能摆动 2–4 倍（见 AGENTS.md 里 Dusk 那十二项门槛）。
    /// 它的用处是**把一次事件里的两个阶段分开**——"输入处理慢"和"处理完到出画慢"
    /// 症状不同，而合在一起量就永远分不开。
    /// </remarks>
    internal static IDisposable Scope(string area, string what, double warnMs = 0) =>
        new ScopeToken(area, what, warnMs);

    private static void Append(string line)
    {
        var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_path is null || !string.Equals(_day, day, StringComparison.Ordinal))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanStartWrite", "logs");
            Directory.CreateDirectory(dir);
            _day = day;
            _path = Path.Combine(dir, $"app-{day}.log");
            Enabled = true;
        }

        RollIfTooBig();
        File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
    }

    private static void RollIfTooBig()
    {
        if (_path is null) return;
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        File.Move(_path, _path + ".1", overwrite: true);
    }

    private sealed class ScopeToken(string area, string what, double warnMs) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _watch.Stop();
            var ms = _watch.Elapsed.TotalMilliseconds;
            Write(area, string.Create(
                CultureInfo.InvariantCulture,
                $"{(warnMs > 0 && ms >= warnMs ? "!" : " ")}{what} {ms:F2}ms"));
        }
    }
}

namespace WeeklyQuotaMonitor;

/// <summary>两张时间序列图共享的实际分析窗口，边界均为包含端点的绝对时间。</summary>
public readonly record struct ChartTimeWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>按本地时刻求当前监控日；未到指定起点时沿用昨天起点，边界只改变显示不重置数据。</summary>
    public static ChartTimeWindow CurrentDay(DateTimeOffset now, int startMinutes)
    {
        if (startMinutes is < 0 or >= 1440) throw new ArgumentOutOfRangeException(nameof(startMinutes));
        var localNow = now.LocalDateTime;
        var start = localNow.Date.AddMinutes(startMinutes);
        if (start > localNow) start = start.AddDays(-1);
        return new(new DateTimeOffset(start), now);
    }
}

/// <summary>把绘图区内的滚轮和拖拽转换为时间窗口；不持有业务数据，由页面收到事件后重新计算。</summary>
public sealed class ChartTimeNavigation : IDisposable
{
    private readonly Control _owner;
    private RectangleF _plot;
    private ChartTimeWindow? _window;
    private DateTimeOffset? _firstSample;
    private DateTimeOffset? _lastSample;
    private ChartTimeWindow? _dragWindow;
    private float _dragX;
    private float _dragWidth;
    private long _lastMove;
    public event Action<ChartTimeWindow>? WindowChanged;
    public event Action? ResetRequested;
    public ChartTimeWindow? Window => _window;
    public bool IsDragging => _dragWindow.HasValue;

    /// <summary>绑定控件鼠标事件；滚轮仅在绘图区消费，不干扰侧边页面滚动。</summary>
    public ChartTimeNavigation(Control owner)
    {
        _owner = owner; owner.TabStop = true;
        owner.MouseWheel += Wheel; owner.MouseDown += Down; owner.MouseMove += Move;
        owner.MouseUp += Up; owner.MouseDoubleClick += DoubleClick;
        owner.MouseCaptureChanged += CaptureChanged;
        owner.KeyDown += KeyDown; owner.PreviewKeyDown += PreviewKeyDown;
        owner.GotFocus += FocusChanged; owner.LostFocus += FocusChanged;
    }

    /// <summary>设置已知采样边界；单点保留其前方一秒，不向未来补出空白。</summary>
    public void SetDataBounds(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (first.HasValue && last.HasValue && first > last) throw new ArgumentException("采样边界顺序错误。");
        _firstSample = first; _lastSample = last;
        if (first.HasValue && last.HasValue && last.Value - first.Value < TimeSpan.FromSeconds(1))
            _firstSample = last.Value.AddSeconds(-1);
    }

    /// <summary>页面刷新时约束实际窗口并返回同一边界供统计使用；不回发事件，避免重算循环。</summary>
    public ChartTimeWindow SetWindow(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start) end = start.AddSeconds(1);
        _window = Constrain(new(start, end));
        return _window.Value;
    }

    /// <summary>超出采样边界时整体平移窗口，放大到全范围后停止；保留真实间隙，不制造未来时间。</summary>
    private ChartTimeWindow Constrain(ChartTimeWindow window)
    {
        if (_firstSample is null && _lastSample is null) return window;
        var minimum = _firstSample?.UtcTicks ?? DateTimeOffset.FromUnixTimeMilliseconds((long)Minimum).UtcTicks;
        var maximum = _lastSample?.UtcTicks ?? DateTimeOffset.FromUnixTimeMilliseconds((long)Maximum).UtcTicks;
        var span = Math.Clamp((window.End - window.Start).Ticks, TimeSpan.TicksPerSecond, maximum - minimum);
        var start = Math.Clamp(window.Start.UtcTicks, minimum, maximum - span);
        return new(new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(start + span, TimeSpan.Zero));
    }

    /// <summary>使用本次实际绘图区计算交互，空数据时仍允许继续缩放、平移和恢复。</summary>
    public void SetPlot(RectangleF plot) => _plot = plot;

    /// <summary>保持鼠标锚点对应时间不动地缩放；最小一秒，限制在日期控件可表达的区间内。</summary>
    public static ChartTimeWindow Zoom(ChartTimeWindow window, double fraction, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        fraction = Math.Clamp(fraction, 0, 1);
        var span = (window.End - window.Start).TotalMilliseconds;
        var nextSpan = Math.Clamp(span * factor, 1000, Maximum - Minimum);
        return Bound(window.Start.ToUnixTimeMilliseconds() + fraction * (span - nextSpan), nextSpan);
    }

    /// <summary>按原始拖拽窗口平移，保持跨度不变，避免逐次舍入累积误差。</summary>
    public static ChartTimeWindow Pan(ChartTimeWindow window, double fraction) => Bound(
        window.Start.ToUnixTimeMilliseconds() + (window.End - window.Start).TotalMilliseconds * fraction,
        (window.End - window.Start).TotalMilliseconds);

    private static readonly double Minimum = new DateTimeOffset(1753, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly double Maximum = new DateTimeOffset(9998, 12, 30, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>在有效日期范围内整体平移窗口，而不是分别裁边导致跨度漂移。</summary>
    private static ChartTimeWindow Bound(double start, double span)
    {
        span = Math.Clamp(span, 1000, Maximum - Minimum);
        start = Math.Clamp(start, Minimum, Maximum - span);
        return new(DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(start)),
            DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(start + span)));
    }

    /// <summary>把方向键作为图表输入，避免系统先将它们用于移动到其他控件。</summary>
    private void PreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home) e.IsInputKey = true;
    }

    /// <summary>提供与鼠标等价的时间导航；按键只作用于当前图表，Home恢复所选范围。</summary>
    private void KeyDown(object? sender, KeyEventArgs e)
    {
        if (_window is not { } window) return;
        switch (e.KeyCode)
        {
            case Keys.Left: Publish(Pan(window, -0.15)); break;
            case Keys.Right: Publish(Pan(window, 0.15)); break;
            case Keys.Add: case Keys.Oemplus: Publish(Zoom(window, 0.5, 1 / 1.2)); break;
            case Keys.Subtract: case Keys.OemMinus: Publish(Zoom(window, 0.5, 1.2)); break;
            case Keys.Home: ResetRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }

    /// <summary>焦点变化立即重绘，供图表显示独立焦点边框。</summary>
    private void FocusChanged(object? sender, EventArgs e) => _owner.Invalidate();

    /// <summary>滚轮向上放大、向下缩小，更新后的窗口直接交给业务页重算。</summary>
    private void Wheel(object? sender, MouseEventArgs e)
    {
        if (_window is not { } window || !_plot.Contains(e.Location) || _plot.Width <= 0) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        Publish(Zoom(window, (e.X - _plot.Left) / _plot.Width, Math.Pow(1.2, -e.Delta / 120d)));
    }

    /// <summary>左键开始拖拽时捕获鼠标，指针离开图表仍能完成一次平移。</summary>
    private void Down(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1 || !_plot.Contains(e.Location) || _window is null) return;
        _owner.Focus();
        _dragWindow = _window; _dragX = e.X; _dragWidth = _plot.Width;
        _owner.Capture = true; _owner.Cursor = Cursors.SizeWE;
    }

    /// <summary>拖动期间约三十毫秒刷新一次业务窗口，避免鼠标高频事件重复大量聚合。</summary>
    private void Move(object? sender, MouseEventArgs e)
    {
        if (_dragWindow is not { } initial || Math.Abs(e.X - _dragX) < 2 || Environment.TickCount64 - _lastMove < 30) return;
        _lastMove = Environment.TickCount64;
        Publish(Pan(initial, (_dragX - e.X) / _dragWidth));
    }

    /// <summary>抬起时提交最终位置，不能因为刷新节流遗漏最后一段位移。</summary>
    private void Up(object? sender, MouseEventArgs e)
    {
        if (_dragWindow is not { } initial || e.Button != MouseButtons.Left) return;
        _dragWindow = null; _owner.Capture = false; _owner.Cursor = Cursors.Default;
        if (Math.Abs(e.X - _dragX) >= 2) Publish(Pan(initial, (_dragX - e.X) / _dragWidth));
    }

    /// <summary>双击恢复页面所选的初始范围，恢复规则由业务页负责。</summary>
    private void DoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_plot.Contains(e.Location)) return;
        _dragWindow = null; _owner.Capture = false; _owner.Cursor = Cursors.Default; ResetRequested?.Invoke();
    }

    /// <summary>窗口失去鼠标捕获时结束拖拽，避免下一次移动产生跳变。</summary>
    private void CaptureChanged(object? sender, EventArgs e)
    {
        if (_owner.Capture) return;
        _dragWindow = null; _owner.Cursor = Cursors.Default;
    }

    /// <summary>先更新当前窗口再通知页面，连续滚轮以最新窗口为基准。</summary>
    private void Publish(ChartTimeWindow window) { _window = Constrain(window); WindowChanged?.Invoke(_window.Value); }

    /// <summary>移除拥有者事件订阅，不创建后台线程或额外轮询。</summary>
    public void Dispose()
    {
        _owner.MouseWheel -= Wheel; _owner.MouseDown -= Down; _owner.MouseMove -= Move; _owner.MouseUp -= Up;
        _owner.MouseDoubleClick -= DoubleClick; _owner.MouseCaptureChanged -= CaptureChanged;
        _owner.KeyDown -= KeyDown; _owner.PreviewKeyDown -= PreviewKeyDown;
        _owner.GotFocus -= FocusChanged; _owner.LostFocus -= FocusChanged;
    }
}

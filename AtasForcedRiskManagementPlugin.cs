using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontWeights = System.Windows.FontWeights;
using WpfResizeMode = System.Windows.ResizeMode;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextAlignment = System.Windows.TextAlignment;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextWrapping = System.Windows.TextWrapping;
using WpfThickness = System.Windows.Thickness;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;
using WpfWindow = System.Windows.Window;
using WpfWindowState = System.Windows.WindowState;
using WpfWindowStyle = System.Windows.WindowStyle;
using WpfOrientation = System.Windows.Controls.Orientation;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies.Chart;
using OFT.Rendering.Control;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace AtasForcedRiskManagementPlugin;

[DisplayName("ATAS Forced Risk Manager")]
[Category("Risk Management")]
public class AtasForcedRiskManagementPlugin : ChartStrategy
{
    private static readonly HttpClient ProductVersionHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private const string ProductSlug = "atas-forced-risk-management-plugin";
    private const string CurrentProductVersion = "1.0.0";
    private const string ProductVersionApiUrl = "https://tradinghubs.org/api/products/latest-version";
    private const string BlockerText = "Risk limit reached";
    private const string BlockerSubtitle = "Trading is paused. Review your risk plan before continuing.";
    private const uint TokenAdjustPrivileges = 0x00000020;
    private const uint TokenQuery = 0x00000008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint ShutdownReasonPlannedApplication = 0x80000000u | 0x00040000u | 0x00000001u;
    private const string SeShutdownName = "SeShutdownPrivilege";
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(5);

    private readonly RenderFont _titleFont = new RenderFont("Arial", 42f, System.Drawing.FontStyle.Bold);
    private readonly RenderFont _subtitleFont = new RenderFont("Arial", 18f, System.Drawing.FontStyle.Regular);
    private readonly RenderFont _updateFont = new RenderFont("Arial", 10f, System.Drawing.FontStyle.Regular);
    private readonly RenderStringFormat _centerFormat = new RenderStringFormat
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    private readonly object _sync = new object();

    private volatile bool _productVersionCheckStarted;
    private volatile bool _productUpdateAvailable;
    private decimal _maxLossAmount = 500m;
    private int _maxConsecutiveLosses = 3;
    private bool _autoShutdownEnabled = true;
    private bool _triggered;
    private int _popupShown;
    private int _fullscreenBlockerShown;
    private int _riskResponseRequested;
    private int _shutdownRequested;
    private bool _hasClosedPnlSnapshot;
    private bool _hasSessionBaseline;
    private int _consecutiveLosses;
    private decimal _previousClosedPnl;
    private decimal _sessionBaselineTotalPnl;
    private decimal _lastOpenPnl;
    private decimal _lastClosedPnl;
    private decimal _lastTotalPnl;
    private decimal _lastSessionPnl;
    private decimal _lastLossAmount;
    private string? _lastAccountId;
    private string? _sessionBaselineAccountId;
    private string _triggerReason = string.Empty;
    private string _latestProductVersion = string.Empty;
    private Portfolio? _currentPortfolio;

    [Display(Name = "Max Loss Amount", GroupName = "Risk", Description = "Maximum loss since strategy start. Uses the ClosedPnL + OpenPnL delta, so floating loss is included.", Order = 10)]
    [Range(typeof(decimal), "0", "999999999")]
    public decimal MaxLossAmount
    {
        get => _maxLossAmount;
        set
        {
            _maxLossAmount = Math.Max(0m, value);
            UpdateRiskFromLivePortfolio(redraw: true);
        }
    }

    [Display(Name = "Max Consecutive Losses", GroupName = "Risk", Description = "Maximum consecutive losing ClosedPnL updates before the blocker triggers.", Order = 20)]
    [Range(0, 1000)]
    public int MaxConsecutiveLosses
    {
        get => _maxConsecutiveLosses;
        set
        {
            _maxConsecutiveLosses = Math.Max(0, value);
            UpdateRiskFromLivePortfolio(redraw: true);
        }
    }

    [Display(Name = "Enable Windows Shutdown", GroupName = "Risk", Description = "Default on. When enabled, hitting a risk limit forces Windows shutdown for a cooldown break.", Order = 30)]
    public bool EnableWindowsShutdown
    {
        get => _autoShutdownEnabled;
        set => _autoShutdownEnabled = value;
    }

    public AtasForcedRiskManagementPlugin()
        : base(useCandles: true)
    {
        EnableCustomDrawing = true;
        DrawAbovePrice = true;
        DenyToChangePanel = true;
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        MaybeCheckProductVersionAsync();
    }

    protected override void OnStarted()
    {
        base.OnStarted();

        lock (_sync)
            ResetRiskSessionTracking();

        UpdateRiskFromLivePortfolio(redraw: true);
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();

        if (TradingManager == null)
            return;

        TradingManager.PortfolioSelected += OnPortfolioSelected;
        _currentPortfolio = TradingManager.Portfolio;
    }

    protected override void OnDispose()
    {
        if (TradingManager != null)
            TradingManager.PortfolioSelected -= OnPortfolioSelected;

        base.OnDispose();
    }

    protected override void OnStopped()
    {
        base.OnStopped();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        MaybeCheckProductVersionAsync();
        UpdateRiskFromLivePortfolio(redraw: true);
    }

    public override bool ProcessMouseDown(RenderControlMouseEventArgs e)
    {
        return base.ProcessMouseDown(e);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        return base.ProcessMouseMove(e);
    }

    public override bool ProcessMouseUp(RenderControlMouseEventArgs e)
    {
        return base.ProcessMouseUp(e);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (Container == null)
            return;

        UpdateRiskFromLivePortfolio(redraw: false);

        Rectangle region = Container.Region;
        if (!_triggered)
        {
            DrawProductUpdateNotice(context, region);
            return;
        }

        DrawBlockerOverlay(context, region);
    }

    private void MaybeCheckProductVersionAsync()
    {
        if (_productVersionCheckStarted)
            return;

        _productVersionCheckStarted = true;
        _ = CheckProductVersionAsync();
    }

    private async Task CheckProductVersionAsync()
    {
        try
        {
            string url = $"{ProductVersionApiUrl}?product_slug={Uri.EscapeDataString(ProductSlug)}&current_version={Uri.EscapeDataString(CurrentProductVersion)}";
            using var response = await ProductVersionHttpClient.GetAsync(url).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                return;

            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!TryReadString(root, "latest_version", out string latestVersion))
                return;

            bool updateAvailable = TryReadBool(root, "update_available", out bool apiUpdateAvailable)
                ? apiUpdateAvailable
                : CompareProductVersions(latestVersion, CurrentProductVersion) != 0;
            if (!updateAvailable)
                return;

            _latestProductVersion = latestVersion;
            _productUpdateAvailable = true;
            TryRedrawChart();
        }
        catch
        {
        }
    }

    private void DrawProductUpdateNotice(RenderContext context, Rectangle area)
    {
        if (!_productUpdateAvailable || string.IsNullOrWhiteSpace(_latestProductVersion))
            return;

        string text = $"ATAS Forced Risk Manager {_latestProductVersion} update available / 可更新";
        Size size = context.MeasureString(text, _updateFont);
        context.DrawString(text, _updateFont, Color.Orange, new Rectangle(area.Left + 8, area.Top + 8, size.Width, size.Height));
    }

    private void DrawBlockerOverlay(RenderContext context, Rectangle area)
    {
        context.FillRectangle(Color.FromArgb(255, 0, 0, 0), area);

        int centerY = area.Top + area.Height / 2;
        Rectangle titleRect = new Rectangle(area.Left, centerY - 70, area.Width, 64);
        Rectangle subtitleRect = new Rectangle(area.Left, centerY + 6, area.Width, 36);

        context.DrawString(BlockerText, _titleFont, Color.FromArgb(255, 255, 255, 255), titleRect, _centerFormat);
        context.DrawString(BlockerSubtitle, _subtitleFont, Color.FromArgb(255, 210, 210, 210), subtitleRect, _centerFormat);
    }

    private void UpdateRiskFromLivePortfolio(bool redraw)
    {
        bool riskTriggeredNow = false;

        lock (_sync)
        {
            decimal openPnl;
            decimal closedPnl;
            string? accountId;

            if (!TryReadPortfolioPnl(out accountId, out openPnl, out closedPnl))
                return;

            decimal totalPnl = closedPnl + openPnl;
            EnsureSessionBaseline(accountId, totalPnl, closedPnl);
            UpdateConsecutiveLosses(closedPnl);

            _lastAccountId = accountId;
            _lastOpenPnl = openPnl;
            _lastClosedPnl = closedPnl;
            _lastTotalPnl = totalPnl;
            _lastSessionPnl = totalPnl - _sessionBaselineTotalPnl;
            _lastLossAmount = _lastSessionPnl < 0m ? -_lastSessionPnl : 0m;

            bool lossLimitHit = _maxLossAmount > 0m && _lastLossAmount >= _maxLossAmount;
            bool streakLimitHit = _maxConsecutiveLosses > 0 && _consecutiveLosses >= _maxConsecutiveLosses;

            if (!_triggered && (lossLimitHit || streakLimitHit))
            {
                _triggered = true;
                riskTriggeredNow = true;
                _triggerReason = lossLimitHit
                    ? "Max loss amount reached."
                    : "Max consecutive losses reached.";
            }
        }

        if (riskTriggeredNow)
            StartRiskResponse();

        if (redraw)
            RedrawChart();
    }

    private bool TryReadPortfolioPnl(out string? accountId, out decimal openPnl, out decimal closedPnl)
    {
        accountId = null;
        openPnl = 0m;
        closedPnl = 0m;

        try
        {
            Portfolio? portfolio = _currentPortfolio ?? TradingManager?.Portfolio ?? this.Portfolio;
            if (portfolio == null)
                return false;

            accountId = portfolio.AccountID;
            openPnl = portfolio.OpenPnL;
            closedPnl = portfolio.ClosedPnL;
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ATASForcedRiskManagement] Portfolio PnL read failed: " + ex.Message);
            return false;
        }
    }

    private void OnPortfolioSelected(Portfolio portfolio)
    {
        lock (_sync)
        {
            _currentPortfolio = portfolio;
            _lastAccountId = portfolio.AccountID;
            _lastOpenPnl = portfolio.OpenPnL;
            _lastClosedPnl = portfolio.ClosedPnL;
            _lastTotalPnl = portfolio.ClosedPnL + portfolio.OpenPnL;
            SetSessionBaseline(portfolio.AccountID, _lastTotalPnl, portfolio.ClosedPnL);
            _lastSessionPnl = 0m;
            _lastLossAmount = 0m;
        }

        RedrawChart();
    }

    private void EnsureSessionBaseline(string? accountId, decimal totalPnl, decimal closedPnl)
    {
        if (_hasSessionBaseline && string.Equals(_sessionBaselineAccountId, accountId, StringComparison.Ordinal))
            return;

        SetSessionBaseline(accountId, totalPnl, closedPnl);
    }

    private void SetSessionBaseline(string? accountId, decimal totalPnl, decimal closedPnl)
    {
        _sessionBaselineAccountId = accountId;
        _sessionBaselineTotalPnl = totalPnl;
        _hasSessionBaseline = true;
        _previousClosedPnl = closedPnl;
        _hasClosedPnlSnapshot = true;
        _consecutiveLosses = 0;
    }

    private void ResetRiskSessionTracking()
    {
        _hasSessionBaseline = false;
        _sessionBaselineAccountId = null;
        _sessionBaselineTotalPnl = 0m;
        _hasClosedPnlSnapshot = false;
        _previousClosedPnl = 0m;
        _consecutiveLosses = 0;
        _lastSessionPnl = 0m;
        _lastLossAmount = 0m;
        _triggered = false;
        _triggerReason = string.Empty;
        Interlocked.Exchange(ref _popupShown, 0);
        Interlocked.Exchange(ref _fullscreenBlockerShown, 0);
        Interlocked.Exchange(ref _riskResponseRequested, 0);
        Interlocked.Exchange(ref _shutdownRequested, 0);
    }

    private void UpdateConsecutiveLosses(decimal closedPnl)
    {
        if (!_hasClosedPnlSnapshot)
        {
            _previousClosedPnl = closedPnl;
            _hasClosedPnlSnapshot = true;
            return;
        }

        decimal delta = closedPnl - _previousClosedPnl;
        if (delta < 0m)
            _consecutiveLosses++;
        else if (delta > 0m)
            _consecutiveLosses = 0;

        _previousClosedPnl = closedPnl;
    }

    private void StartRiskResponse()
    {
        if (Interlocked.Exchange(ref _riskResponseRequested, 1) == 1)
            return;

        _ = Task.Run(RunRiskResponseAsync);
    }

    private async Task RunRiskResponseAsync()
    {
        DateTime popupTriggeredUtc = DateTime.UtcNow;
        ShowBlockingPopup();

        await CancelOrdersAndCloseCurrentPositionAsync().ConfigureAwait(false);

        if (!_autoShutdownEnabled)
            return;

        TimeSpan remainingDelay = ShutdownDelay - (DateTime.UtcNow - popupTriggeredUtc);
        if (remainingDelay > TimeSpan.Zero)
            await Task.Delay(remainingDelay).ConfigureAwait(false);

        RequestWindowsShutdown();
    }

    private void ShowBlockingPopup()
    {
        if (Interlocked.Exchange(ref _popupShown, 1) == 1)
            return;

        ShowFullscreenBlocker();
    }

    private void ShowFullscreenBlocker()
    {
        if (Interlocked.Exchange(ref _fullscreenBlockerShown, 1) == 1)
            return;

        Thread thread = new Thread(ShowFullscreenBlockerWindow)
        {
            IsBackground = true,
            Name = "AtasForcedRiskManagementPluginBlocker"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void ShowFullscreenBlockerWindow()
    {
        try
        {
            WpfTextBlock titleBlock = new WpfTextBlock
            {
                Text = BlockerText,
                Foreground = WpfBrushes.White,
                FontSize = 72,
                FontWeight = WpfFontWeights.Bold,
                TextAlignment = WpfTextAlignment.Center,
                TextWrapping = WpfTextWrapping.Wrap,
                VerticalAlignment = WpfVerticalAlignment.Center,
                Margin = new WpfThickness(40)
            };

            WpfTextBlock subtitleBlock = new WpfTextBlock
            {
                Text = BlockerSubtitle,
                Foreground = WpfBrushes.LightGray,
                FontSize = 24,
                TextAlignment = WpfTextAlignment.Center,
                TextWrapping = WpfTextWrapping.Wrap,
                Margin = new WpfThickness(40, 8, 40, 0)
            };

            WpfStackPanel content = new WpfStackPanel
            {
                Orientation = WpfOrientation.Vertical,
                VerticalAlignment = WpfVerticalAlignment.Center
            };
            content.Children.Add(titleBlock);
            content.Children.Add(subtitleBlock);

            WpfWindow window = new WpfWindow
            {
                WindowStyle = WpfWindowStyle.None,
                WindowState = WpfWindowState.Maximized,
                ResizeMode = WpfResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                Background = WpfBrushes.Black,
                Content = content
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ATASForcedRiskManagement] Fullscreen blocker failed: " + ex.Message);
        }
    }

    private void RequestWindowsShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            return;

        ShutdownWindows();
    }

    private async Task CancelOrdersAndCloseCurrentPositionAsync()
    {
        try
        {
            Order[] workingOrders = (Orders ?? Enumerable.Empty<Order>())
                .Where(IsCancelableCurrentAccountOrder)
                .ToArray();

            foreach (Order order in workingOrders)
            {
                try
                {
                    await CancelOrderAsync(order).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("[ATASForcedRiskManagement] Cancel order failed: " + ex.Message);
                }
            }

            if (!HasCurrentPosition())
                return;

            if (TradingManager == null)
                throw new InvalidOperationException("Trading manager is not available.");

            Position? position = TradingManager.Position;
            if (position == null || !IsCurrentPosition(position))
                throw new InvalidOperationException("Current position is not available.");

            await TradingManager.ClosePositionAsync(position, false, true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ATASForcedRiskManagement] Risk response trading actions failed: " + ex.Message);
        }
    }

    private bool IsCancelableCurrentAccountOrder(Order order)
    {
        if (order.State != OrderStates.Active)
            return false;

        OrderStatus status = order.Status();
        if (status is OrderStatus.Filled or OrderStatus.Canceled)
            return false;

        return IsCurrentAccountOrder(order);
    }

    private bool IsCurrentAccountOrder(Order order)
    {
        Portfolio? portfolio = _currentPortfolio ?? TradingManager?.Portfolio ?? Portfolio;
        if (portfolio == null)
            return true;

        if (ReferenceEquals(order.Portfolio, portfolio))
            return true;

        return !string.IsNullOrWhiteSpace(order.AccountID) &&
               string.Equals(order.AccountID, portfolio.AccountID, StringComparison.Ordinal);
    }

    private bool HasCurrentPosition()
    {
        if (CurrentPosition != 0m)
            return true;

        Position? position = TradingManager?.Position;
        return position != null && IsCurrentPosition(position) && position.IsInPosition && position.Volume != 0m;
    }

    private bool IsCurrentPosition(Position position)
    {
        Portfolio? portfolio = _currentPortfolio ?? TradingManager?.Portfolio ?? Portfolio;
        Security? security = TradingManager?.Security ?? Security;

        bool portfolioMatches = portfolio == null ||
                                ReferenceEquals(position.Portfolio, portfolio) ||
                                (!string.IsNullOrWhiteSpace(position.AccountID) &&
                                 string.Equals(position.AccountID, portfolio.AccountID, StringComparison.Ordinal));

        bool securityMatches = security == null ||
                               ReferenceEquals(position.Security, security) ||
                               (!string.IsNullOrWhiteSpace(position.SecurityId) &&
                                string.Equals(position.SecurityId, security.SecurityId, StringComparison.Ordinal));

        return portfolioMatches && securityMatches;
    }

    private void ShutdownWindows()
    {
        try
        {
            if (TryInitiateNativeShutdown())
                return;

            StartShutdownExeFallback();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("[ATASForcedRiskManagement] Windows shutdown request failed: " + ex.Message);
        }
    }

    private bool TryInitiateNativeShutdown()
    {
        if (!EnableShutdownPrivilege())
            return false;

        bool started = InitiateSystemShutdownExW(
            null,
            null,
            0,
            true,
            false,
            ShutdownReasonPlannedApplication);

        if (started)
            return true;

        Trace.TraceWarning("[ATASForcedRiskManagement] InitiateSystemShutdownExW failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return false;
    }

    private static bool EnableShutdownPrivilege()
    {
        IntPtr tokenHandle = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out tokenHandle))
                return false;

            if (!LookupPrivilegeValueW(null, SeShutdownName, out Luid luid))
                return false;

            TokenPrivileges privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            bool adjusted = AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            return adjusted && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
                CloseHandle(tokenHandle);
        }
    }

    private static void StartShutdownExeFallback()
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.SystemDirectory + "\\shutdown.exe",
            Arguments = "/s /f /t 0",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadBool(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out JsonElement property))
            return false;

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out value);
    }

    private static int CompareProductVersions(string left, string right)
    {
        string normalizedLeft = NormalizeProductVersion(left);
        string normalizedRight = NormalizeProductVersion(right);

        if (Version.TryParse(normalizedLeft, out Version? leftVersion) &&
            Version.TryParse(normalizedRight, out Version? rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProductVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        Match match = Regex.Match(version.Trim(), @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : version.Trim();
    }

    private void TryRedrawChart()
    {
        try
        {
            RedrawChart();
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InitiateSystemShutdownExW(string? machineName, string? message, uint timeout, bool forceAppsClosed, bool rebootAfterShutdown, uint reason);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

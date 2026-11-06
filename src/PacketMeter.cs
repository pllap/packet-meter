using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// 프로그램의 시작점입니다. 창 없이도 알림 영역에서 실행되도록 메시지 루프를 시작합니다.
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    // Windows Forms를 초기화하고 전체 동작을 관리하는 MeterContext를 실행합니다.
    [STAThread] private static void Main()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MeterContext());
    }
}

// 사용자별 설정 파일에서 측정 주기를 읽고 저장합니다.
internal sealed class MeterSettings
{
    public const int DefaultIntervalMs = 1000;
    public const int MinimumIntervalMs = 100;
    public const int MaximumIntervalMs = 60000;
    private const string IntervalSettingKey = "interval_ms";

    public int Interval = DefaultIntervalMs;
    // 사용자 계정별 설정 파일 위치입니다.
    public static string PathName
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PacketMeter", "settings.ini"); }
    }

    // 파일이 없거나 유효하지 않은 주기는 기본값으로 처리합니다.
    public static MeterSettings Load(string settingsFilePath)
    {
        MeterSettings settings = new MeterSettings();
        try
        {
            if (!File.Exists(settingsFilePath)) return settings;
            foreach (string settingLine in File.ReadAllLines(settingsFilePath))
            {
                string[] settingParts = settingLine.Split(new char[] { '=' }, 2);
                if (settingParts.Length != 2) continue;
                int intervalMs;
                if (settingParts[0] == IntervalSettingKey && int.TryParse(settingParts[1], out intervalMs) &&
                    intervalMs >= MinimumIntervalMs && intervalMs <= MaximumIntervalMs)
                    settings.Interval = intervalMs;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return settings;
    }
    // 측정 주기만 교체하고 이전 버전의 다른 설정 줄은 보존합니다.
    public void Save(string settingsFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath));
        List<string> settingLines = File.Exists(settingsFilePath)
            ? new List<string>(File.ReadAllLines(settingsFilePath)) : new List<string>();
        settingLines.RemoveAll(settingLine => settingLine.StartsWith(IntervalSettingKey + "=", StringComparison.Ordinal));
        settingLines.Add(IntervalSettingKey + "=" + Interval.ToString(CultureInfo.InvariantCulture));
        File.WriteAllLines(settingsFilePath, settingLines.ToArray());
    }
}

// 초당 측정값을 읽기 좋은 문자열과 선풍기 회전 속도로 변환합니다.
internal static class RateFormat
{
    private const double PacketsPerThousand = 1000.0;
    private const double PacketsPerMillion = 1000000.0;
    private const double AbbreviatePacketsAt = 10000.0;
    private const double BytesPerUnit = 1024.0;
    // 전송량 기준: 10 KB/s, 128 KB/s, 1 MB/s, 10 MB/s, 100 MB/s.
    // 각 기준에 대응하는 값은 선풍기의 초당 전체 회전 수입니다.
    private static readonly double[] ReferenceBytesPerSecond =
    {
        10 * BytesPerUnit, 128 * BytesPerUnit, BytesPerUnit * BytesPerUnit,
        10 * BytesPerUnit * BytesPerUnit, 100 * BytesPerUnit * BytesPerUnit
    };
    private static readonly double[] ReferenceRotationsPerSecond = { 0.0, 0.12, 0.30, 0.80, 0.90 };

    // 패킷 수는 1,000 단위로 축약합니다.
    public static string Packets(double packetsPerSecond)
    {
        if (packetsPerSecond < AbbreviatePacketsAt) return Math.Round(packetsPerSecond).ToString("N0") + " p/s";
        if (packetsPerSecond < PacketsPerMillion) return (packetsPerSecond / PacketsPerThousand).ToString("0.#") + "k p/s";
        return (packetsPerSecond / PacketsPerMillion).ToString("0.#") + "M p/s";
    }
    // 바이트 수는 1,024 단위로 변환합니다.
    public static string Bytes(double bytesPerSecond)
    {
        string[] unitLabels = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        int unitIndex = 0;
        while (bytesPerSecond >= BytesPerUnit && unitIndex < unitLabels.Length - 1)
        {
            bytesPerSecond /= BytesPerUnit;
            unitIndex++;
        }
        return bytesPerSecond.ToString(unitIndex == 0 ? "0" : "0.#") + " " + unitLabels[unitIndex];
    }
    // 전송량 기준점 사이를 로그 비율로 보간해 초당 회전 수를 구합니다.
    public static double RotationsPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond <= ReferenceBytesPerSecond[0]) return 0;
        for (int index = 1; index < ReferenceBytesPerSecond.Length; index++)
        {
            if (bytesPerSecond <= ReferenceBytesPerSecond[index])
            {
                double interpolation = Math.Log(bytesPerSecond / ReferenceBytesPerSecond[index - 1]) /
                    Math.Log(ReferenceBytesPerSecond[index] / ReferenceBytesPerSecond[index - 1]);
                return ReferenceRotationsPerSecond[index - 1] + interpolation *
                    (ReferenceRotationsPerSecond[index] - ReferenceRotationsPerSecond[index - 1]);
            }
        }
        return ReferenceRotationsPerSecond[ReferenceRotationsPerSecond.Length - 1];
    }
}

// 알림 영역에 표시할 선풍기 그림을 투명 비트맵으로 만듭니다.
internal static class FanArt
{
    public const int FrameCount = 48;
    public const int BladeCount = 3;
    private const int IconSizePixels = 32;
    private const float IconCenterPixels = IconSizePixels / 2f;
    private const float DegreesPerBlade = 120f;
    private const float RimStrokePixels = 1.8f;
    private const float RimInsetPixels = 1.7f;
    private const float RimDiameterPixels = 28.6f;
    private const float HubDiameterPixels = 5f;

    // 같은 모양의 날개 세 개가 120도마다 반복되므로 120도 구간만 프레임으로 만듭니다.
    public static Bitmap Draw(int frameIndex, bool lightTheme)
    {
        Bitmap bitmap = new Bitmap(IconSizePixels, IconSizePixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            Color iconColor = lightTheme ? Color.Black : Color.White;
            using (Pen rimPen = new Pen(iconColor, RimStrokePixels))
            using (Brush bladeBrush = new SolidBrush(iconColor))
            {
                graphics.DrawEllipse(rimPen, RimInsetPixels, RimInsetPixels, RimDiameterPixels, RimDiameterPixels);
                for (int bladeIndex = 0; bladeIndex < BladeCount; bladeIndex++)
                {
                    GraphicsState originalState = graphics.Save();
                    graphics.TranslateTransform(IconCenterPixels, IconCenterPixels);
                    graphics.RotateTransform(frameIndex * (DegreesPerBlade / FrameCount) + bladeIndex * DegreesPerBlade);
                    using (GraphicsPath bladeShape = new GraphicsPath())
                    {
                        // 중심을 원점으로 한 베지어 곡선 좌표가 날개 외곽을 이룹니다.
                        bladeShape.StartFigure();
                        bladeShape.AddBezier(0, 0, -2, -3, -7, -7, -6, -11);
                        bladeShape.AddBezier(-6, -11, -5, -14, 0, -14, 2, -11);
                        bladeShape.AddBezier(2, -11, 4, -8, 3, -3, 0, 0);
                        bladeShape.CloseFigure();
                        graphics.FillPath(bladeBrush, bladeShape);
                    }
                    graphics.Restore(originalState);
                }
                float hubLeft = IconCenterPixels - HubDiameterPixels / 2;
                graphics.FillEllipse(bladeBrush, hubLeft, hubLeft, HubDiameterPixels, HubDiameterPixels);
            }
        }
        return bitmap;
    }
}

// 클릭했을 때 네 가지 초당 속도와 현재 어댑터를 보여 주는 일반 창입니다.
internal sealed class DetailWindow : Form
{
    private readonly Label adapterNameLabel = new Label();
    private readonly Label statusLabel = new Label();
    private readonly Label receivedPacketsLabel = new Label(), sentPacketsLabel = new Label();
    private readonly Label receivedBytesLabel = new Label(), sentBytesLabel = new Label();

    // 창과 각 설명·수치 라벨을 배치합니다. 위치 숫자는 이 창 안의 픽셀 좌표입니다.
    public DetailWindow()
    {
        Text = "Packet Meter"; ClientSize = new Size(330, 185);
        MinimumSize = new Size(346, 224); StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        adapterNameLabel.AutoEllipsis = true; adapterNameLabel.SetBounds(16, 15, 298, 22);
        statusLabel.AutoEllipsis = true; statusLabel.SetBounds(16, 154, 298, 20);
        Label packets = new Label { Text = "패킷", AutoSize = true, Location = new Point(104, 45) };
        Label bytes = new Label { Text = "바이트", AutoSize = true, Location = new Point(218, 45) };
        Label inLabel = new Label { Text = "IN 수신", AutoSize = true, Location = new Point(16, 77) };
        Label outLabel = new Label { Text = "OUT 송신", AutoSize = true, Location = new Point(16, 115) };
        receivedPacketsLabel.SetBounds(104, 77, 110, 24); sentPacketsLabel.SetBounds(104, 115, 110, 24);
        receivedBytesLabel.SetBounds(218, 77, 105, 24); sentBytesLabel.SetBounds(218, 115, 105, 24);
        Controls.AddRange(new Control[] { adapterNameLabel, statusLabel, packets, bytes, inLabel, outLabel, receivedPacketsLabel, sentPacketsLabel, receivedBytesLabel, sentBytesLabel });
    }
    // 측정 가능 여부에 따라 네 속도를 표시하거나 --로 표시합니다.
    public void UpdateValues(string adapterName, bool hasValidSample, double receivedPacketsPerSecond, double sentPacketsPerSecond, double receivedBytesPerSecond, double sentBytesPerSecond)
    {
        adapterNameLabel.Text = "어댑터: " + adapterName;
        receivedPacketsLabel.Text = hasValidSample ? RateFormat.Packets(receivedPacketsPerSecond) : "--";
        sentPacketsLabel.Text = hasValidSample ? RateFormat.Packets(sentPacketsPerSecond) : "--";
        receivedBytesLabel.Text = hasValidSample ? RateFormat.Bytes(receivedBytesPerSecond) : "--";
        sentBytesLabel.Text = hasValidSample ? RateFormat.Bytes(sentBytesPerSecond) : "--";
        statusLabel.Text = hasValidSample ? "모든 값은 초당 속도입니다." : "연결을 확인하거나 다음 갱신을 기다리는 중";
    }
}

// 알림 아이콘, 측정, 애니메이션, 테마와 사용자 입력을 연결하는 프로그램의 중심입니다.
internal sealed class MeterContext : ApplicationContext
{
    private const int AnimationIntervalMs = 33;
    private const int ThemeCheckIntervalMs = 2000;
    private const double AdapterCacheSeconds = 2.0;
    private const double ForceAdapterRefreshSeconds = -10.0;
    private const int MaximumTooltipLength = 63;

    // 비트맵에서 얻은 임시 네이티브 아이콘 핸들을 해제합니다.
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr iconHandle);
    private readonly NotifyIcon trayIcon = new NotifyIcon();
    private readonly ContextMenuStrip contextMenu = new ContextMenuStrip();
    private readonly Timer sampleTimer = new Timer(), animationTimer = new Timer(), themeTimer = new Timer();
    private readonly Stopwatch uptimeClock = Stopwatch.StartNew();
    private readonly MeterSettings settings = MeterSettings.Load(MeterSettings.PathName);
    private Icon[] iconFrames;
    private DetailWindow detailWindow;
    private NetworkInterface cachedInterface;
    private string selectedAdapterId, previousAdapterId, adapterName = "연결된 어댑터 없음";
    private double lastAdapterCheckSeconds = ForceAdapterRefreshSeconds;
    private double previousSampleTimeSeconds, previousAnimationTimeSeconds, animationPhase, rotationsPerSecond;
    private long previousReceivedPackets, previousSentPackets, previousReceivedBytes, previousSentBytes;
    private double receivedPacketsPerSecond, sentPacketsPerSecond, receivedBytesPerSecond, sentBytesPerSecond;
    private bool hasValidSample;
    private int currentFrameIndex;
    private bool lightTheme;

    // 설정과 프레임을 준비하고 타이머 및 트레이 아이콘의 이벤트를 연결합니다.
    public MeterContext()
    {
        contextMenu.Opening += delegate { BuildMenu(); };
        trayIcon.ContextMenuStrip = contextMenu;
        trayIcon.Text = "Packet Meter";
        lightTheme = IsLightTheme();
        iconFrames = CreateIcons(lightTheme);
        trayIcon.Icon = iconFrames[currentFrameIndex];
        trayIcon.Visible = true;
        trayIcon.MouseClick += delegate(object sender, MouseEventArgs mouseEvent)
        {
            if (mouseEvent.Button == MouseButtons.Left) ShowDetails();
        };
        sampleTimer.Interval = settings.Interval;
        sampleTimer.Tick += delegate { Sample(); };
        animationTimer.Interval = AnimationIntervalMs;
        animationTimer.Tick += delegate { Animate(); };
        themeTimer.Interval = ThemeCheckIntervalMs;
        themeTimer.Tick += delegate { RefreshTheme(); };
        previousAnimationTimeSeconds = uptimeClock.Elapsed.TotalSeconds;
        Sample();
        sampleTimer.Start();
        themeTimer.Start();
    }

    // 현재 사용 중인 이더넷, Wi-Fi, PPP 어댑터를 찾습니다.
    private static NetworkInterface[] Interfaces()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
            (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet || adapter.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
             adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp)).ToArray();
    }
    // 자동 선택을 위해 어댑터에 기본 게이트웨이가 있는지 확인합니다.
    private static bool HasGateway(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address != null &&
                !gateway.Address.Equals(System.Net.IPAddress.Any) && !gateway.Address.Equals(System.Net.IPAddress.IPv6Any));
        }
        catch (NetworkInformationException) { return false; }
    }

    // 직접 선택한 어댑터 또는 게이트웨이가 있는 첫 어댑터를 최대 2초간 재사용합니다.
    private NetworkInterface CurrentInterface()
    {
        if (uptimeClock.Elapsed.TotalSeconds - lastAdapterCheckSeconds < AdapterCacheSeconds) return cachedInterface;
        lastAdapterCheckSeconds = uptimeClock.Elapsed.TotalSeconds;
        NetworkInterface[] availableAdapters = Interfaces();
        cachedInterface = selectedAdapterId == null ? availableAdapters.FirstOrDefault(HasGateway) : availableAdapters.FirstOrDefault(adapter => adapter.Id == selectedAdapterId);
        return cachedInterface;
    }
    // 누적 카운터의 차이를 실제 경과 시간으로 나누고 아이콘과 상세 창에 전달합니다.
    private void Sample()
    {
        try
        {
            NetworkInterface networkInterface = CurrentInterface();
            if (networkInterface == null)
            {
                adapterName = selectedAdapterId == null ? "활성 어댑터 없음" : "선택한 어댑터가 연결되지 않음";
                hasValidSample = false;
                previousAdapterId = null;
            }
            else
            {
                IPv4InterfaceStatistics interfaceStatistics = networkInterface.GetIPv4Statistics();
                long receivedPackets = interfaceStatistics.UnicastPacketsReceived + interfaceStatistics.NonUnicastPacketsReceived;
                long sentPackets = interfaceStatistics.UnicastPacketsSent + interfaceStatistics.NonUnicastPacketsSent;
                long receivedBytes = interfaceStatistics.BytesReceived;
                long sentBytes = interfaceStatistics.BytesSent;
                double currentTimeSeconds = uptimeClock.Elapsed.TotalSeconds;
                adapterName = networkInterface.Name;
                // 첫 측정, 어댑터 변경, 카운터 초기화 후에는 비교할 기준이 없습니다.
                hasValidSample = previousAdapterId == networkInterface.Id && currentTimeSeconds > previousSampleTimeSeconds &&
                    receivedPackets >= previousReceivedPackets && sentPackets >= previousSentPackets &&
                    receivedBytes >= previousReceivedBytes && sentBytes >= previousSentBytes;
                if (hasValidSample)
                {
                    double elapsedSeconds = currentTimeSeconds - previousSampleTimeSeconds;
                    receivedPacketsPerSecond = (receivedPackets - previousReceivedPackets) / elapsedSeconds;
                    sentPacketsPerSecond = (sentPackets - previousSentPackets) / elapsedSeconds;
                    receivedBytesPerSecond = (receivedBytes - previousReceivedBytes) / elapsedSeconds;
                    sentBytesPerSecond = (sentBytes - previousSentBytes) / elapsedSeconds;
                }
                previousAdapterId = networkInterface.Id;
                previousReceivedPackets = receivedPackets;
                previousSentPackets = sentPackets;
                previousReceivedBytes = receivedBytes;
                previousSentBytes = sentBytes;
                previousSampleTimeSeconds = currentTimeSeconds;
            }
        }
        catch (NetworkInformationException)
        {
            hasValidSample = false;
            previousAdapterId = null;
            adapterName = "네트워크 정보를 읽을 수 없음";
        }

        // 속도를 바꾸기 전에 이전 속도로 지난 시간만큼 회전을 먼저 반영합니다.
        Animate();
        rotationsPerSecond = hasValidSample ? RateFormat.RotationsPerSecond(receivedBytesPerSecond + sentBytesPerSecond) : 0;
        if (rotationsPerSecond > 0 && !animationTimer.Enabled) animationTimer.Start();
        else if (rotationsPerSecond == 0 && animationTimer.Enabled) animationTimer.Stop();
        string tooltipText = hasValidSample ? "IN " + RateFormat.Bytes(receivedBytesPerSecond) + " / OUT " + RateFormat.Bytes(sentBytesPerSecond) : "Packet Meter: 연결 확인 중";
        trayIcon.Text = tooltipText.Length > MaximumTooltipLength ? tooltipText.Substring(0, MaximumTooltipLength) : tooltipText;
        if (detailWindow != null && !detailWindow.IsDisposed)
            detailWindow.UpdateValues(adapterName, hasValidSample, receivedPacketsPerSecond, sentPacketsPerSecond, receivedBytesPerSecond, sentBytesPerSecond);
    }

    // 회전 위치를 실제 경과 시간만큼 이동시키고 바뀐 프레임만 표시합니다.
    private void Animate()
    {
        double currentTimeSeconds = uptimeClock.Elapsed.TotalSeconds;
        double elapsedSeconds = currentTimeSeconds - previousAnimationTimeSeconds;
        previousAnimationTimeSeconds = currentTimeSeconds;
        // 같은 날개가 3개이므로 전체 회전 1번에 같은 모양의 구간이 3번 반복됩니다.
        animationPhase = (animationPhase + elapsedSeconds * rotationsPerSecond * FanArt.BladeCount) % 1;
        int nextFrameIndex = (int)(animationPhase * FanArt.FrameCount);
        if (nextFrameIndex != currentFrameIndex)
        {
            currentFrameIndex = nextFrameIndex;
            trayIcon.Icon = iconFrames[currentFrameIndex];
        }
    }
    // Windows 시스템 테마를 읽습니다. 앱 테마가 아니라 작업 표시줄에 적용되는 테마입니다.
    private static bool IsLightTheme()
    {
        try
        {
            using (RegistryKey personalizationKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                return personalizationKey != null && Convert.ToInt32(personalizationKey.GetValue("SystemUsesLightTheme", 0)) != 0;
            }
        }
        catch (Exception error)
        {
            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException) && !(error is FormatException) && !(error is InvalidCastException)) throw;
            return false;
        }
    }
    // 프레임을 한 번만 생성해 재사용하고, 임시 비트맵 핸들은 즉시 해제합니다.
    private static Icon[] CreateIcons(bool isLightTheme)
    {
        Icon[] createdIcons = new Icon[FanArt.FrameCount];
        try
        {
            for (int frameIndex = 0; frameIndex < createdIcons.Length; frameIndex++)
            using (Bitmap frameBitmap = FanArt.Draw(frameIndex, isLightTheme))
            {
                IntPtr iconHandle = frameBitmap.GetHicon();
                try { using (Icon borrowedIcon = Icon.FromHandle(iconHandle)) createdIcons[frameIndex] = (Icon)borrowedIcon.Clone(); }
                finally { DestroyIcon(iconHandle); }
            }
            return createdIcons;
        }
        catch
        {
            foreach (Icon icon in createdIcons) if (icon != null) icon.Dispose();
            throw;
        }
    }
    // 테마가 바뀌었을 때만 새 색상의 프레임을 만들고 기존 프레임을 해제합니다.
    private void RefreshTheme()
    {
        bool newLightTheme = IsLightTheme();
        if (newLightTheme == lightTheme) return;
        Icon[] newIconFrames = CreateIcons(newLightTheme);
        trayIcon.Icon = newIconFrames[currentFrameIndex];
        Icon[] previousIcons = iconFrames;
        iconFrames = newIconFrames;
        lightTheme = newLightTheme;
        foreach (Icon icon in previousIcons) icon.Dispose();
    }
    // 상세 창이 없으면 만들고, 이미 열려 있으면 최신 값을 보여 주며 활성화합니다.
    private void ShowDetails()
    {
        if (detailWindow == null || detailWindow.IsDisposed) detailWindow = new DetailWindow();
        detailWindow.UpdateValues(adapterName, hasValidSample, receivedPacketsPerSecond, sentPacketsPerSecond, receivedBytesPerSecond, sentBytesPerSecond);
        if (!detailWindow.Visible) detailWindow.Show();
        if (detailWindow.WindowState == FormWindowState.Minimized) detailWindow.WindowState = FormWindowState.Normal;
        detailWindow.Activate();
    }
    // 메뉴를 열 때 현재 어댑터 목록으로 다시 만들고 이전 메뉴 자원을 정리합니다.
    private void BuildMenu()
    {
        while (contextMenu.Items.Count > 0)
        {
            ToolStripItem menuItem = contextMenu.Items[0];
            contextMenu.Items.RemoveAt(0);
            menuItem.Dispose();
        }
        ToolStripMenuItem adapterTitleMenuItem = new ToolStripMenuItem("현재: " + adapterName) { Enabled = false };
        contextMenu.Items.Add(adapterTitleMenuItem);
        ToolStripMenuItem openDetailsMenuItem = new ToolStripMenuItem("속도 보기");
        openDetailsMenuItem.Click += delegate { ShowDetails(); };
        contextMenu.Items.Add(openDetailsMenuItem);
        ToolStripMenuItem adaptersMenuItem = new ToolStripMenuItem("네트워크 어댑터");
        ToolStripMenuItem automaticSelectionItem = new ToolStripMenuItem("자동 선택 (기본 게이트웨이)") { Checked = selectedAdapterId == null };
        automaticSelectionItem.Click += delegate { SelectInterface(null); };
        adaptersMenuItem.DropDownItems.Add(automaticSelectionItem);
        NetworkInterface[] availableAdapters;
        try { availableAdapters = Interfaces(); }
        catch (NetworkInformationException) { availableAdapters = new NetworkInterface[0]; }
        foreach (NetworkInterface networkInterface in availableAdapters.OrderBy(adapter => adapter.Name))
        {
            string adapterId = networkInterface.Id;
            ToolStripMenuItem menuItem = new ToolStripMenuItem(networkInterface.Name) { Checked = selectedAdapterId == adapterId };
            menuItem.Click += delegate { SelectInterface(adapterId); };
            adaptersMenuItem.DropDownItems.Add(menuItem);
        }
        contextMenu.Items.Add(adaptersMenuItem);
        ToolStripMenuItem settingsMenuItem = new ToolStripMenuItem("갱신 주기 설정...");
        settingsMenuItem.Click += delegate { ConfigureInterval(); };
        contextMenu.Items.Add(settingsMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("종료");
        exitMenuItem.Click += delegate { ExitThread(); };
        contextMenu.Items.Add(exitMenuItem);
    }

    // null은 자동 선택입니다. 이전 어댑터의 카운터와 비교하지 않도록 기준을 지웁니다.
    private void SelectInterface(string adapterId)
    {
        selectedAdapterId = adapterId;
        previousAdapterId = null;
        lastAdapterCheckSeconds = ForceAdapterRefreshSeconds;
        Sample();
    }

    // 갱신 주기를 입력받아 저장하고, 성공하면 측정 타이머에 적용합니다.
    private void ConfigureInterval()
    {
        using (Form dialog = new Form { Text = "갱신 주기 설정", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(330, 142), Font = SystemFonts.MessageBoxFont })
        {
            Label label = new Label { Text = "갱신 주기 (ms)", AutoSize = true, Location = new Point(16, 21) };
            NumericUpDown intervalInput = new NumericUpDown { Minimum = MeterSettings.MinimumIntervalMs, Maximum = MeterSettings.MaximumIntervalMs, Increment = MeterSettings.MinimumIntervalMs, Value = settings.Interval, Location = new Point(160, 18), Width = 110 };
            Label hint = new Label { Text = "100~60,000 ms · 모든 수치는 초당 속도", AutoSize = true, Location = new Point(16, 54) };
            Button save = new Button { Text = "저장", DialogResult = DialogResult.OK, Location = new Point(159, 99), Size = new Size(75, 27) };
            Button cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(243, 99), Size = new Size(75, 27) };
            dialog.Controls.AddRange(new Control[] { label, intervalInput, hint, save, cancel });
            dialog.AcceptButton = save;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog() != DialogResult.OK) return;
            int previousIntervalMs = settings.Interval;
            settings.Interval = (int)intervalInput.Value;
            try { settings.Save(MeterSettings.PathName); }
            catch (Exception error)
            {
                settings.Interval = previousIntervalMs;
                if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
                MessageBox.Show("설정을 저장하지 못했습니다.\n" + error.Message, "Packet Meter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            sampleTimer.Interval = settings.Interval;
            previousAdapterId = null;
            Sample();
        }
    }

    // 종료할 때 타이머, 트레이 아이콘, 이미지와 창을 모두 해제합니다.
    protected override void ExitThreadCore()
    {
        sampleTimer.Stop();
        animationTimer.Stop();
        themeTimer.Stop();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        contextMenu.Dispose();
        sampleTimer.Dispose();
        animationTimer.Dispose();
        themeTimer.Dispose();
        if (iconFrames != null) foreach (Icon icon in iconFrames) icon.Dispose();
        if (detailWindow != null && !detailWindow.IsDisposed) detailWindow.Dispose();
        base.ExitThreadCore();
    }
}

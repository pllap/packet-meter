using System;
using System.Drawing;
using System.IO;

internal static class PacketMeterTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Main(string[] args)
    {
        Check(args.Length == 1, "테스트 출력 폴더가 필요합니다.");
        Check(RateFormat.Packets(42) == "42 p/s", "패킷 속도 형식");
        Check(RateFormat.Bytes(2048) == "2 KB/s", "바이트 속도 형식");
        Check(RateFormat.RotationsPerSecond(10240) == 0, "정지 기준");
        Check(Math.Abs(RateFormat.RotationsPerSecond(10 * 1024 * 1024) - 0.8) < 0.00001, "10 MB/s 회전 속도");
        double previousSpeed = -1;
        foreach (double bytesPerSecond in new double[] { 0, 10240, 20480, 131072, 1048576, 10485760, 104857600, 1073741824 })
        {
            double speed = RateFormat.RotationsPerSecond(bytesPerSecond);
            Check(speed >= previousSpeed && speed <= 0.9, "회전 속도는 단조 증가하고 상한 이하여야 합니다.");
            previousSpeed = speed;
        }

        string settingsFile = Path.Combine(args[0], "settings-test.ini");
        try
        {
            File.WriteAllText(settingsFile, "unit=bytes\r\ninterval_ms=1000\r\n");
            MeterSettings settings = MeterSettings.Load(settingsFile);
            settings.Interval = 250;
            settings.Save(settingsFile);
            Check(MeterSettings.Load(settingsFile).Interval == 250, "주기 설정 저장·복원");
            Check(File.ReadAllText(settingsFile).Contains("unit=bytes"), "기존 설정 보존");
            File.WriteAllText(settingsFile, "interval_ms=99\r\n");
            Check(MeterSettings.Load(settingsFile).Interval == MeterSettings.DefaultIntervalMs, "잘못된 주기 기본값");
        }
        finally { if (File.Exists(settingsFile)) File.Delete(settingsFile); }

        using (Bitmap darkTheme = FanArt.Draw(0, false))
        using (Bitmap lightTheme = FanArt.Draw(0, true))
        {
            Check(darkTheme.GetPixel(0, 0).A == 0 && lightTheme.GetPixel(0, 0).A == 0, "투명 배경");
            Check(darkTheme.GetPixel(16, 16).R == 255, "다크 테마: 흰색 아이콘");
            Check(lightTheme.GetPixel(16, 16).R == 0, "라이트 테마: 검은색 아이콘");
        }
        Console.WriteLine("All Packet Meter tests passed.");
    }
}

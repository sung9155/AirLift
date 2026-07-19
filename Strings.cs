namespace AirOutput;

/// <summary>UI string table. Language: "ko" or "en".</summary>
public static class L
{
    public static string Lang { get; set; } = "ko";
    private static string T(string ko, string en) => Lang == "en" ? en : ko;

    // Tray menu
    public static string NotConnected => T("연결 안 됨", "Not connected");
    public static string StatusConnecting(string name) => T($"연결 중: {name}...", $"Connecting: {name}...");
    public static string StatusConnected(string name, double db, bool muted)
        => T($"연결됨: {name} ({db:0}dB{(muted ? ", 음소거" : "")})",
             $"Connected: {name} ({db:0}dB{(muted ? ", muted" : "")})");
    public static string Speakers => T("스피커", "Speakers");
    public static string Scanning => T("검색 중...", "Scanning...");
    public static string NoSpeakersFound => T("스피커를 찾지 못함", "No speakers found");
    public static string Rescan => T("다시 검색", "Rescan");
    public static string PasswordSuffix => T(" (비밀번호 - 미지원)", " (password - unsupported)");
    public static string InputSource => T("입력 소스 (캡처할 출력 장치)", "Input source (output device to capture)");
    public static string DefaultDevice => T("(기본 재생 장치)", "(Default playback device)");
    public static string VolumeUp => T("볼륨 +", "Volume +");
    public static string VolumeDown => T("볼륨 -", "Volume -");
    public static string Mute => T("음소거", "Mute");
    public static string LatencyMenu => T("지연 모드", "Latency mode");
    public static string LatencyUltra => T("초저지연 (약 0.15초, 안정적인 네트워크)", "Ultra-low (~0.15 s, solid network)");
    public static string LatencyLow => T("저지연 (약 0.25초)", "Low (~0.25 s)");
    public static string LatencyStable => T("안정 (약 0.45초, 여유 버퍼)", "Stable (~0.45 s, larger buffer)");
    public static string AutoReconnect => T("연결 끊기면 자동 재연결", "Auto-reconnect when connection drops");
    public static string Reconnecting(string name) => T($"재연결 중: {name}...", $"Reconnecting: {name}...");
    public static string BalloonReconnected => T("재연결됨", "Reconnected");
    public static string Disconnect => T("연결 해제", "Disconnect");
    public static string AutoConnect => T("시작 시 마지막 스피커 자동 연결", "Auto-connect to last speaker at startup");
    public static string AutoSwitch => T("연결 시 기본 출력 자동 전환", "Switch default output while connected");
    public static string RunAtStartup => T("Windows 시작 시 실행", "Run at Windows startup");
    public static string VbCableMenu => T("가상 출력 장치(VB-CABLE) 안내...", "Virtual output device (VB-CABLE) info...");
    public static string LanguageMenu => "언어 / Language";
    public static string Exit => T("종료", "Exit");

    // Balloons / dialogs
    public static string BalloonConnected => T("연결됨", "Connected");
    public static string BalloonConnectFailed => T("연결 실패", "Connection failed");
    public static string BalloonDisconnected => T("연결 해제됨", "Disconnected");
    public static string BalloonDropped => T("연결 끊김", "Connection lost");
    public static string StreamingStopped => T("AirPlay 스트리밍을 중지했습니다.", "AirPlay streaming stopped.");
    public static string InputLabel(string name) => T($"입력: {name}", $"Input: {name}");
    public static string MutedLabel => T("음소거", "Muted");
    public static string AlreadyRunning =>
        T("AirOutput이 이미 실행 중입니다. 트레이 아이콘을 확인하세요.",
          "AirOutput is already running. Check the tray icon.");
    public static string VbDialogTitle => T("AirOutput - 가상 출력 장치", "AirOutput - Virtual output device");
    public static string VbDialogBody(bool installed, string? deviceName) =>
        T("AirPlay 스피커를 Windows '출력 장치'처럼 쓰려면 가상 오디오 케이블이 필요합니다.\n\n" +
          "1. VB-CABLE 드라이버 설치 (무료)\n" +
          "2. Windows 소리 설정에서 출력 장치를 \"CABLE Input\"으로 선택\n" +
          "3. AirOutput의 [입력 소스]에서 \"CABLE Input\" 선택 (설치돼 있으면 자동 선택)\n" +
          "4. 스피커 연결 - 이제 모든 소리가 AirPlay로만 나갑니다\n\n" +
          (installed ? $"현재 상태: 가상 케이블 감지됨 ({deviceName})"
                     : "현재 상태: 가상 케이블 없음. 다운로드 페이지를 열까요?"),
          "To use an AirPlay speaker like a Windows output device, a virtual audio cable is required.\n\n" +
          "1. Install the VB-CABLE driver (free)\n" +
          "2. In Windows sound settings, select \"CABLE Input\" as the output device\n" +
          "3. In AirOutput's [Input source], select \"CABLE Input\" (auto-selected if installed)\n" +
          "4. Connect a speaker - all sound now goes to AirPlay only\n\n" +
          (installed ? $"Status: virtual cable detected ({deviceName})"
                     : "Status: no virtual cable found. Open the download page?"));

    // Engine / protocol errors
    public static string StreamError(string message) => T($"전송 오류: {message}", $"Stream error: {message}");
    public static string SessionLost(string message) => T($"RTSP 세션 끊김: {message}", $"RTSP session lost: {message}");
    public static string PasswordRequired(string what) =>
        T($"{what} 거부됨: 비밀번호/페어링 필요 (미지원)",
          $"{what} rejected: password/pairing required (unsupported)");
    public static string VolumeChangeFailed(string message) => T($"볼륨 변경 실패: {message}", $"Volume change failed: {message}");
}

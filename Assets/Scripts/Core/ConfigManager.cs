using UnityEngine;

/// <summary>
/// Settings.txt(CSVReader)의 데이터를 기반으로 디스플레이를 초기화하고 환경 변수를 관리하는 클래스
/// CSVReader(-1000) 이후, MediaScanner.Awake() 실행 전에 반드시 완료되어야 한다.
/// </summary>
[DefaultExecutionOrder(-900)]
public class ConfigManager : MonoBehaviour
{
    public static int DisplayCount { get; private set; } = 1;

    // 로딩 스파이크 방지를 위한 전역 타이머/딜레이 관련 수치 (Settings.txt에서 덮어씌움 가능)
    public static float CrossfadeDelay { get; private set; } = 1.0f;

    public static float SyncTimeoutSeconds { get; private set; } = 3.0f;
    public static float AsyncLoadDelay { get; private set; } = 0.2f;

    // 감시 시스템(Watchdog) 임계치 관련 수치
    public static float StallThreshold { get; private set; } = 1.0f;

    public static int MaxStallRetries { get; private set; } = 3;

    // 화면에 콘텐츠를 어떻게 맞출지 설정 (stretch / fit_outside / fit_inside)
    public static string DisplayFitMode { get; private set; } = "fit_outside";

    public static string ImageFitMode { get; private set; } = "fit_outside";

    // 동영상 음소거 설정
    public static bool MuteAudio { get; private set; } = false;

    // 외부 설정(CSV)에서 파싱된 전역 확장자 리스트
    public static string[] AllowedVideoExtensions { get; private set; } = { "mp4", "mov", "webm", "mkv", "avi" };

    public static string[] AllowedImageExtensions { get; private set; } = { "jpg", "jpeg", "png" };

    private void Awake()
    {
        ApplySettings();
    }

    /// <summary>
    /// CSVReader로부터 로드된 캐싱 데이터를 현재 해상도 및 디스플레이 시스템에 적용합니다.
    /// </summary>
    private void ApplySettings()
    {
        DisplayCount = CSVReader.GetIntValue("DisplayCount", Display.displays.Length);

        // 1. 전역 타이머 / 딜레이 관련 수치 파싱
        CrossfadeDelay = CSVReader.GetFloatValue("Crossfade_Delay", 1.0f);
        SyncTimeoutSeconds = CSVReader.GetFloatValue("Sync_Timeout", 3.0f);
        AsyncLoadDelay = CSVReader.GetFloatValue("Async_Load_Delay", 0.2f);
        StallThreshold = CSVReader.GetFloatValue("Stall_Threshold", 1.0f);
        MaxStallRetries = CSVReader.GetIntValue("Max_Stall_Retries", 3);

        // 화면 맞춤 모드 파싱 (stretch / fit_outside / fit_inside)
        string fitRaw = CSVReader.GetStringValue("Display_Fit_Mode");
        if (!string.IsNullOrEmpty(fitRaw))
        {
            DisplayFitMode = fitRaw.ToLower().Trim();
        }

        // 이미지 전용 화면 맞춤 모드 파싱
        string imgFitRaw = CSVReader.GetStringValue("Image_Fit_Mode");
        if (!string.IsNullOrEmpty(imgFitRaw))
        {
            ImageFitMode = imgFitRaw.ToLower().Trim();
        }

        // 동영상 음소거 설정 파싱
        MuteAudio = CSVReader.GetStringValue("Mute_Audio")?.ToLower().Trim() == "true";

        Debug.LogWarning($"[ConfigManager] 설정 로드됨 - 크로스페이드: {CrossfadeDelay}초, 분산로드: {AsyncLoadDelay}초, 프리징감지: {StallThreshold}초, 재시도: {MaxStallRetries}회, 영상맞춤: {DisplayFitMode}, 이미지맞춤: {ImageFitMode}, 음소거: {MuteAudio}");

        // 2. 외부 확장자 파싱 (Settings.txt에 Video_Extensions, Image_Extensions 명시 시 덮어씌움)
        string videoExtRaw = CSVReader.GetStringValue("Video_Extensions");
        if (!string.IsNullOrEmpty(videoExtRaw))
        {
            // 세미콜론(;) 구분: CSV 파일 자체가 쉼표 형식이므로 확장자 구분은 세미콜론 사용
            AllowedVideoExtensions = videoExtRaw.ToLower().Replace(" ", "").Split(';');
            Debug.Log($"[ConfigManager] 비디오 지원 확장자 업데이트됨: {string.Join(", ", AllowedVideoExtensions)}");
        }

        string imageExtRaw = CSVReader.GetStringValue("Image_Extensions");
        if (!string.IsNullOrEmpty(imageExtRaw))
        {
            // 세미콜론(;) 구분: CSV 파일 자체가 쉼표 형식이므로 확장자 구분은 세미콜론 사용
            AllowedImageExtensions = imageExtRaw.ToLower().Replace(" ", "").Split(';');
            Debug.Log($"[ConfigManager] 이미지 지원 확장자 업데이트됨: {string.Join(", ", AllowedImageExtensions)}");
        }

        // 유니티 엔진이 로드할 수 있는 지원 디스플레이(연결된 모니터) 수만큼만 반복
        for (int i = 0; i < ConfigManager.DisplayCount; i++)
        {
            string resStr = CSVReader.GetStringValue($"Display{i}_Resolution");

            if (!string.IsNullOrEmpty(resStr))
            {
                var parts = resStr.Split('x');

                // 정규성 검사: "1920x1080" 형태인지 확인하고 올바른 숫자 변환이 가능한지 시도 (TryParse)
                if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
                {
                    if (i > 0)
                    {
                        // 서브 디스플레이를 지정된 해상도로 활성화 (RefreshRate 대신 단순히 지원되는 Activate 호출로 경고 완화)
                        RefreshRate rr = new RefreshRate() { numerator = 60, denominator = 1 };
                        Display.displays[i].Activate(width, height, rr);
                        Debug.Log($"[ConfigManager] 디스플레이 {i} 활성화 확인: {width}x{height}");
                    }
                    else
                    {
                        // 메인 디스플레이(0번)는 창 크기/전체화면 해상도 적용
                        Screen.SetResolution(width, height, FullScreenMode.FullScreenWindow);
                        Debug.Log($"[ConfigManager] 메인 디스플레이 해상도 적용 완료: {width}x{height}");
                    }
                }
                else
                {
                    // [예외 환경 대비]: 포맷 형식이 틀린 경우
                    Debug.LogWarning($"[ConfigManager] 디스플레이 {i} 해상도 파싱 실패 (잘못된 형식, 예: 1920x1080 필요): {resStr}");
                }
            }
            else if (i > 0)
            {
                // 해상도 값이 없더라도 물리적으로 연결되어 있다면 강제로 기본 해상도 활성화 시도
                Display.displays[i].Activate();
                Debug.Log($"[ConfigManager] 디스플레이 {i} 세팅이 없지만 연결되어 기본 해상도로 활성화됨.");
            }
        }
    }
}
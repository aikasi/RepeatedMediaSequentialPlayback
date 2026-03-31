using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// AVPro Video 사용 선언
using RenderHeads.Media.AVProVideo;

/// <summary>
/// 다중 모니터의 재생 동기화, 더블 버퍼링 전환, 비동기 메모리 관리를 통괄하는 메인 컨트롤러.
/// </summary>
public class PlaybackManager : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private MediaScanner mediaScanner;

    // 모니터별 렌더링 컴포넌트 (자동 생성됨, Inspector 할당 불필요)
    private DisplayRenderer[] displayRenderers;

    // 모니터 인덱스별로 현재 돌아가는 인덱스를 추적합니다.
    private int currentGlobalSequenceIndex = 0;

    // 더블 버퍼링: A(0), B(1) 등 토글되는 버퍼 인덱스 (현재 시청 화면)
    private int currentBufferIndex = 0;

    // 모니터별로 2개(버퍼A, 버퍼B)의 MediaPlayer를 관리하는 2차원 자료구조
    // array[monitorID][bufferID]
    private MediaPlayer[][] mediaPlayers;

    // 시계열 멈춤 방지 및 동기화 무한 대기 타임아웃 변수
    private bool isTransitioning = false;

    // 이미지 전시용 실시간 타이머 (AVPro는 이미지의 재생 시간을 추적하지 않으므로 별도 관리)
    private float _imageDisplayStartTime = -1f;

    // 백그라운드 선행 로딩 코루틴 추적 변수 (에러 시 중단 및 대체 목적)
    private Coroutine _prepareCoroutine;

    [Header("모니터 할당 및 확장 관련 설정")]
    [Tooltip("에러 발생 시 메인 모니터 화면 위에 띄울 경고창 프리팹")]
    [SerializeField] private ErrorOverlayController errorOverlayPrefab;

    private void Start()
    {
        // [Guard] Inspector에 MediaScanner이 할당되어 있는지 사전 방어
        if (mediaScanner == null)
        {
            Debug.LogError("[PlaybackManager] MediaScanner가 Inspector에 할당되지 않았습니다. 실행을 중단합니다.");
            return;
        }
        // MediaScanner의 초기 1회 로드 완료를 기다린 후 셋업을 시작.
        StartCoroutine(InitializePlaybackSystem());
    }

    private IEnumerator InitializePlaybackSystem()
    {
        // Scanner 파싱 대기 (업시 알넉첨 데이 다음 프레임 외는 안전성 확보)
        // [Guard] 미디어 클리파일이 전혀 없을 경우 무한대기를 막기 위한 타임아웃 추가
        int waitFrames = 0;
        while (mediaScanner.Playlist.Count == 0)
        {
            if (++waitFrames > 300)
            {
                Debug.LogError("[PlaybackManager] 미디어 폴더가 비어있거나 스컈 타임아웃. 초기화를 중단합니다.");
                yield break;
            }
            yield return null;
        }

        int monitorCount = CSVReader.GetIntValue("DisplayCount", Display.displays.Length);
        mediaPlayers = new MediaPlayer[monitorCount][];
        displayRenderers = new DisplayRenderer[monitorCount]; // [FIX] 매니페스트 배열 초기화 (누락되어 NullRef 발생하던 부분)

        // 1. 모니터별로 핑퐁 구조 조립
        for (int i = 0; i < monitorCount; i++)
        {
            mediaPlayers[i] = new MediaPlayer[2];

            // --- [UI] Canvas 및 RawImage 동적 생성 ---
            GameObject canvasObj = new GameObject($"Canvas_Monitor_{i}");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = i;
            canvas.sortingOrder = 0;
            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasObj.AddComponent<GraphicRaycaster>();

            RawImage imgA = CreateFullscreenRawImage(canvasObj, "RawImage_BufferA");
            RawImage imgB = CreateFullscreenRawImage(canvasObj, "RawImage_BufferB");

            // --- [AVPro] MediaPlayer 동적 생성 (Watchdog 포함) ---
            for (int buffer = 0; buffer < 2; buffer++)
            {
                GameObject pObj = new GameObject($"Monitor_{i}_Buffer_{buffer}");
                pObj.transform.SetParent(this.transform);

                // AVPro Video의 핵심 컴포넌트 부착
                MediaPlayer player = pObj.AddComponent<MediaPlayer>();
                player.AutoOpen = false;
                player.AutoStart = false;

                // 에러 검출 및 자동 복구를 위한 자체 Watchdog 컴포넌트 부착 및 연동
                VideoWatchdog watchdog = pObj.AddComponent<VideoWatchdog>();
                watchdog.OnFatalError += HandleFatalWatchdogError;

                // 엔진 팝업 억제: 에러 발생 시 Event 시스템으로 넘기도록 자동 처리 옵션
                player.Events.AddListener(OnMediaPlayerEvent);

                mediaPlayers[i][buffer] = player;
            }

            // --- [UI] DisplayRenderer 초기화 (Canvas에 부착하고 MediaPlayer 2개를 주입) ---
            DisplayRenderer dr = canvasObj.AddComponent<DisplayRenderer>();

            // 프리팹이 등록되어 있다면 런타임에 "메인 모니터(0번)"에만 Instantiate 후 주입
            ErrorOverlayController overlayInstance = null;
            if (errorOverlayPrefab != null && i == 0)
            {
                overlayInstance = Instantiate(errorOverlayPrefab, canvas.transform);
                overlayInstance.name = $"ErrorOverlay_MainMonitor";
            }

            dr.Initialize(imgA, imgB, mediaPlayers[i][0], mediaPlayers[i][1], overlayInstance);
            displayRenderers[i] = dr;

            Debug.Log($"[PlaybackManager] 모니터 {i} Canvas + DisplayRenderer 생성 완료");
        }

        // 2. 초기 로딩: 파일번호 1번을 0번 버퍼에 로딩
        currentGlobalSequenceIndex = 1;
        yield return StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, 0));

        // 3. 0번 버퍼(A) 일제히 재생 (크로스페이드 없이 즉시 전시)
        PlayBuffer(0);

        // 4. 보이지 않는 버퍼(B)에 다음 파일번호(2번) 미리 로드
        currentGlobalSequenceIndex = 2;
        _prepareCoroutine = StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, 1));
    }

    private void Update()
    {
        if (isTransitioning) return;
        if (mediaPlayers == null || mediaPlayers.Length == 0) return;

        // 마스터(0번 모니터) 기준 재생 상태 검증
        MediaPlayer masterPlayer = mediaPlayers[0][currentBufferIndex];
        if (masterPlayer == null || masterPlayer.Control == null) return;

        var masterData = TryGetMediaData(0, currentGlobalSequenceIndex - 1);
        bool isMasterFinished = false;

        if (masterData != null && masterData.Type == MediaType.Image)
        {
            // ── 이미지: AVPro 타임라인이 없으므로 실시간 타이머로 전환 감지 ──
            if (_imageDisplayStartTime < 0f)
                _imageDisplayStartTime = Time.time;  // 최초 표시 시각 기록

            float displayDuration = masterData.Duration > 0 ? masterData.Duration : 5f; // 기본 5초
            if (Time.time - _imageDisplayStartTime >= displayDuration)
                isMasterFinished = true;
        }
        else
        {
            // ── 영상: 기존 AVPro 기반 전환 감지 ──
            isMasterFinished = masterPlayer.Control.IsFinished();

            // Duration 강제 설정값이 있다면 추가 체크
            if (masterData != null && masterData.Duration > 0)
            {
                if (masterPlayer.Control.GetCurrentTime() >= masterData.Duration)
                    isMasterFinished = true;
            }
        }

        // [디버그용] Ctrl + 1 키를 누르면 즉각적으로 다음 영상으로 화면 전환 강제 트리거
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
        {
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            {
                Debug.LogWarning("[PlaybackManager] 디버그: Ctrl + 1 입력 감지됨. 다음 시퀀스로 강제 전환합니다.");
                isMasterFinished = true;
            }
        }

        if (isMasterFinished)
        {
            _imageDisplayStartTime = -1f;  // 이미지 타이머 초기화
            CheckAndExecuteTransition();
        }
    }

    /// <summary>
    /// 마스터 영상이 끝났을 때, 다음 순서 영상(숨어있는 버퍼)들이 모두 Ready(Prepare 완료)인지 동기화 검증합니다.
    /// </summary>
    private void CheckAndExecuteTransition()
    {
        int nextBufferIndex = (currentBufferIndex + 1) % 2;

        // 마스터 영상 종료 확인 즉시 전환 진행 (Watchdog이 재생 실패를 별도 감시)
        StartCoroutine(ExecuteCrossfadeAndSwap(nextBufferIndex));
    }

    /// <summary>
    /// 크로스페이드 (알파값 조절) 및 사용 끝난 영상 메모리 해제
    /// </summary>
    private IEnumerator ExecuteCrossfadeAndSwap(int targetBufferIndex)
    {
        isTransitioning = true;
        int oldBufferIndex = currentBufferIndex;

        // 크로스페이드 시작 직전, 현재 재생 중이던 이전 버퍼의 모든 영상을 강제 일시정지!
        // (서브 모니터가 먼저 끝나서 0.0초부터 다시 반복 재생되는 현상 방지)
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            var oldPlayer = mediaPlayers[i][oldBufferIndex];
            if (oldPlayer != null && oldPlayer.Control != null)
            {
                oldPlayer.Pause();
            }
        }

        // 새로운 버퍼 백그라운드에서 같이 재생 시작
        PlayBuffer(targetBufferIndex);

        // [UI] 크로스페이드: 모니터별 DisplayRenderer에 Alpha 트윈 동시 예약
        if (displayRenderers != null)
        {
            for (int r = 0; r < displayRenderers.Length; r++)
            {
                if (displayRenderers[r] != null)
                    StartCoroutine(displayRenderers[r].CrossfadeTo(targetBufferIndex, ConfigManager.CrossfadeDelay));
            }
        }
        yield return new WaitForSeconds(ConfigManager.CrossfadeDelay);

        currentBufferIndex = targetBufferIndex;

        // 과거 버퍼 (방금 재생 끝난 영상 및 이미지) 강제 메모리 해제
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            MediaPlayer oldPlayer = mediaPlayers[i][oldBufferIndex];
            if (oldPlayer.Control != null)
            {
                // AVPro 기본 메모리 파기 및 핸들 반환
                oldPlayer.CloseMedia();

                // CloseMedia() 이후 핸들 반환 완료 시점에 텍스처를 Destroy하여 네이티브 크래시 방지
                if (oldPlayer.TextureProducer != null)
                {
                    Texture currentTexture = oldPlayer.TextureProducer.GetTexture();
                    if (currentTexture != null)
                    {
                        Destroy(currentTexture);
                    }
                }
            }
        }

        // 가비지 컬렉터 강제 호출로 런타임 누수 차단
        Resources.UnloadUnusedAssets();

        // 핑퐁 글로벌 시퀀스 1업 및 다음 영상 백그라운드 비동기 Prepare 지시
        currentGlobalSequenceIndex++;

        if (_prepareCoroutine != null) StopCoroutine(_prepareCoroutine);
        _prepareCoroutine = StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, oldBufferIndex));

        isTransitioning = false;
    }

    private int _lastLoggedLoopIndex = -1;
    private float _lastLoopLogTime = -3600f; // 처음 1회전 시 무조건 1번 찍히도록 초기값 설정

    /// <summary>
    /// I/O 병목 방지를 위해 각 모니터의 하드디스크 엑세스를 순차적으로 지연 로드합니다.
    /// 로딩 전 모든 모니터의 파일 번호(Index) 일치 여부를 사전 검증합니다.
    /// </summary>
    private IEnumerator PrepareNextSequence(int logicalSequenceIndex, int bufferIndex)
    {
        if (mediaScanner.MaxValidIndex == 0)
        {
            Debug.LogWarning("[PlaybackManager] 치명적 오류: 공통으로 재생 가능한 미디어가 없습니다 (MaxValidIndex = 0)");
            yield break;
        }

        // 실제 파일 번호(Index) 계산
        int fileIndex = ((logicalSequenceIndex - 1) % mediaScanner.MaxValidIndex) + 1;

        // 반복 주기마다 한 번만 진입 (더블 버퍼링 중복 방지)
        if (logicalSequenceIndex > 1 && fileIndex == 1 && _lastLoggedLoopIndex != logicalSequenceIndex)
        {
            _lastLoggedLoopIndex = logicalSequenceIndex;

            // 이전 기록 시간으로부터 1시간(3600초) 이상 지났을 때만 실제 기록 (로그 스팸 방지)
            if (Time.time - _lastLoopLogTime >= 3600f)
            {
                _lastLoopLogTime = Time.time;
                string loopMsg = $"[PlaybackManager] 미디어 정상 순환 중 (생존 로그): 최대 {mediaScanner.MaxValidIndex}번 도달 → 01번으로 리셋 진행 (1시간 주기 알림)";

                // 에디터/콘솔에는 일반 정보로 출력 (노란색 Warning 아님)
                Debug.Log(loopMsg);

                // 실제 로그 파일(.log)에는 수동으로 한 줄 밀어넣기 ([Warning] 태그 없이 기록됨)
                if (Logger.Instance != null)
                {
                    Logger.Instance.Enqueue(loopMsg);
                }
            }
        }

        // ── [1패스] 영상만 먼저 로딩 (디코딩 시간 최대 확보) ──
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            MediaData dataToLoad = TryGetMediaData(i, logicalSequenceIndex);

            if (dataToLoad == null)
            {
                // 미디어 없는 모니터: AVPro 상태 정리
                mediaPlayers[i][bufferIndex].CloseMedia();
                Debug.LogWarning($"[PlaybackManager] 파일 {fileIndex}번 모니터 {i}용 미디어 없음 (패스)");
                continue;
            }

            // 이미지는 2패스에서 처리하므로 스킵
            if (dataToLoad.Type == MediaType.Image) continue;

            // 영상: AVPro 로딩 시작 (디코딩이 백그라운드에서 즉시 시작됨)
            mediaPlayers[i][bufferIndex].OpenMedia(new MediaPath(dataToLoad.FilePath, MediaPathType.AbsolutePathOrURL), autoPlay: false);

            // 영상용 fitter 사전 복원 (이전 이미지의 stretch 오염 방지)
            displayRenderers[i].RestoreVideoFitMode(bufferIndex);

            // 모니터 대수에 비례한 디스크 I/O 분산
            yield return new WaitForSeconds(ConfigManager.AsyncLoadDelay);
        }

        // ── [2패스] 이미지 후처리 (가볍고 즉시 완료, yield 불필요) ──
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            MediaData dataToLoad = TryGetMediaData(i, logicalSequenceIndex);
            if (dataToLoad == null || dataToLoad.Type != MediaType.Image) continue;

            // AVPro 상태 정리 후 Unity 네이티브 이미지 로딩
            mediaPlayers[i][bufferIndex].CloseMedia();
            byte[] bytes = System.IO.File.ReadAllBytes(dataToLoad.FilePath);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool loadSuccess = tex.LoadImage(bytes);

            if (!loadSuccess || tex.width <= 2)
            {
                // 깨진 이미지: 메모리 해제 + 로그 + 화면 경고 표시
                string fileName = System.IO.Path.GetFileName(dataToLoad.FilePath);
                string errMsg = $"[PlaybackManager] 이미지 로딩 실패 (손상된 파일): {dataToLoad.FilePath}";
                Debug.LogError(errMsg);
                if (Logger.Instance != null) Logger.Instance.Enqueue(errMsg);
                Destroy(tex);
                // 에러 메시지는 항상 메인 모니터(0번)에만 띄우되, 어느 모니터/폴더 문제인지 명시
                displayRenderers[0].ShowErrorOverlay(
                    $"[모니터 {i} / Media{i} 폴더]\n{fileName} 파일이 손상/누락되었습니다.");
            }
            else
            {
                displayRenderers[i].SetImageTexture(bufferIndex, tex);
            }
        }
    }

    private void PlayBuffer(int bufferIndex)
    {
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            MediaPlayer p = mediaPlayers[i][bufferIndex];
            if (!string.IsNullOrEmpty(p.MediaPath.Path) && p.Control != null)
            {
                // 메인 모니터(0번)는 IsFinished()로 전환을 감지하므로 루프하면 안 됨
                // 서브 모니터(1번~)만 메인 전환 시점까지 반복 재생
                p.Control.SetLooping(i > 0);
                p.Play();
                p.AudioVolume = ConfigManager.MuteAudio ? 0f : 1f;  // Play 후 음소거 적용
            }
            else
            {
                // 파일 유실 등으로 준비되지 않았다면, 현재 시청중인 화면을 계속 Loop 하게 설정
                if (mediaPlayers[i][currentBufferIndex].Control != null)
                    mediaPlayers[i][currentBufferIndex].Control.SetLooping(true);
            }
        }
    }

    private IEnumerator ResetSystemToZero()
    {
        isTransitioning = true;
        currentGlobalSequenceIndex = 1;

        if (_prepareCoroutine != null)
        {
            StopCoroutine(_prepareCoroutine);
            _prepareCoroutine = null;
        }

        // [CRITICAL FIX] 렌더러에 진행 중인 크로스페이드를 강제 중단하고 즉시 0번 버퍼로 화면 전환 지시
        if (displayRenderers != null)
        {
            for (int i = 0; i < displayRenderers.Length; i++)
            {
                if (displayRenderers[i] != null)
                {
                    displayRenderers[i].ForceResetToBuffer(0);
                }
            }
        }

        // 모두 파기 (CloseMedia 전 Null 참조 방어 포함)
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            if (mediaPlayers[i][0].Control != null) mediaPlayers[i][0].CloseMedia();
            if (mediaPlayers[i][1].Control != null) mediaPlayers[i][1].CloseMedia();
        }

        yield return StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, 0));
        PlayBuffer(0);
        currentBufferIndex = 0;
        isTransitioning = false;

        currentGlobalSequenceIndex = 2;
        _prepareCoroutine = StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, 1));
    }

    /// <summary>
    /// Watchdog 감시 시스템에서 더 이상 복구가 불가능할 때 호출되는 치명적 에러 콜백
    /// </summary>
    private void HandleFatalWatchdogError(MediaPlayer mp)
    {
        if (Logger.Instance != null)
        {
            Logger.Instance.Enqueue($"[PlaybackManager] Watchdog 치명적 에러 콜백 수신됨: {mp.MediaPath.Path}. 전체 리셋을 수행합니다.");
        }

        // 이미 트랜지션(리셋) 중이 아니라면 바로 코루틴을 태워 리셋
        if (!isTransitioning)
        {
            StartCoroutine(ResetSystemToZero());
        }
    }

    /// <summary>
    /// AVPro 내부 엔진에서 던지는 치명적 에러 캐치 콜백
    /// (파일 누락, 코덱 미지원 등 LoadFailed 에러 발생 시 즉시 감지)
    /// </summary>
    public void OnMediaPlayerEvent(MediaPlayer mp, MediaPlayerEvent.EventType eventType, ErrorCode errorCode)
    {
        if (eventType == MediaPlayerEvent.EventType.Error)
        {
            string errorMsg = $"[PlaybackManager] AVPro 재생 네이티브 오류 발생: {errorCode} at path {mp.MediaPath.Path}";
            Debug.LogError(errorMsg);

            // 현재 화면에 영사 중인 주력 버퍼인지, 뒤에서 몰래 준비 중인 백그라운드 버퍼인지 검사
            bool isActiveBuffer = false;
            if (mediaPlayers != null)
            {
                for (int i = 0; i < mediaPlayers.Length; i++)
                {
                    if (mediaPlayers[i] != null && mediaPlayers[i][currentBufferIndex] == mp)
                    {
                        isActiveBuffer = true;
                        break;
                    }
                }
            }

            if (isActiveBuffer)
            {
                if (Logger.Instance != null) Logger.Instance.Enqueue(errorMsg + " (활성 화면(Active) 에러: 즉시 시스템 전체 리셋을 수행합니다.)");

                string fileName = System.IO.Path.GetFileName(mp.MediaPath.Path);
                int monitorIndex = GetMonitorIndexFromPlayer(mp, currentBufferIndex);
                displayRenderers[0].ShowErrorOverlay(
                    $"[모니터 {monitorIndex} / Media{monitorIndex} 폴더]\n{fileName} 파일이 손상/누락되었습니다.");

                if (!isTransitioning) StartCoroutine(ResetSystemToZero());
            }
            else
            {
                if (Logger.Instance != null) Logger.Instance.Enqueue(errorMsg + " (백그라운드 에러: 현재 화면을 유지하고 빈 버퍼에 01번 영상 대체를 준비합니다.)");

                string fileName = System.IO.Path.GetFileName(mp.MediaPath.Path);
                int backgroundBuffer = (currentBufferIndex + 1) % 2;
                int monitorIndex = GetMonitorIndexFromPlayer(mp, backgroundBuffer);
                displayRenderers[0].ShowErrorOverlay(
                    $"[모니터 {monitorIndex} / Media{monitorIndex} 폴더]\n{fileName} 백그라운드 영상이 손상/누락되었습니다.");

                StartCoroutine(ReplaceBackgroundWithZero(backgroundBuffer));
            }
        }
    }

    /// <summary>
    /// 주어진 MediaPlayer가 몇 번째 모니터에 속해있는지 찾아 반환하는 헬퍼 메서드
    /// </summary>
    private int GetMonitorIndexFromPlayer(MediaPlayer mp, int bufferIndex)
    {
        if (mediaPlayers == null) return -1;
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            if (mediaPlayers[i] != null && mediaPlayers[i][bufferIndex] == mp)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 백그라운드 영상 로딩 중 심각한 오류가 발생했을 때,
    /// 멀쩡히 방송 중인 화면(Active)을 강제 종료하지 않고 뒤쪽의 고장난 버퍼만 조용히 1번 파일로 대체시킵니다.
    /// </summary>
    private IEnumerator ReplaceBackgroundWithZero(int backgroundBufferIndex)
    {
        // 1. 기존 진행 중이던 선행 로딩 코루틴 안전하게 취소 방어
        if (_prepareCoroutine != null)
        {
            StopCoroutine(_prepareCoroutine);
            _prepareCoroutine = null;
        }

        // 2. 이미 로드 중이거나 에러난 백그라운드 영상들 엔진 깨끗하게 닫기
        for (int i = 0; i < mediaPlayers.Length; i++)
        {
            if (mediaPlayers[i] != null && mediaPlayers[i][backgroundBufferIndex] != null)
            {
                mediaPlayers[i][backgroundBufferIndex].CloseMedia();
            }
        }

        // 3. 글로벌 인덱스 강제 1변경 및 새로 로딩 트리거
        currentGlobalSequenceIndex = 1;
        _prepareCoroutine = StartCoroutine(PrepareNextSequence(currentGlobalSequenceIndex, backgroundBufferIndex));
        yield break;
    }

    /// <summary>
    /// 논리적 시퀀스 번호(1, 2, 3...)를 입력받아 실제 파일 번호(Index)로 변환해 O(1) 조회합니다.
    /// 최대 연속 번호(MaxValidIndex)를 넘어가면 알아서 01번으로 모듈러 순환됩니다.
    /// </summary>
    private MediaData TryGetMediaData(int monitorIndex, int logicalSequenceIndex)
    {
        if (mediaScanner.PlaylistByIndex == null || !mediaScanner.PlaylistByIndex.TryGetValue(monitorIndex, out var dict))
            return null;

        if (mediaScanner.MaxValidIndex <= 0) return null;

        int fileIndex = ((logicalSequenceIndex - 1) % mediaScanner.MaxValidIndex) + 1;

        if (dict.TryGetValue(fileIndex, out var data))
            return data;

        return null;
    }

    /// <summary>
    /// 전체 화면을 꽉 채우는 RawImage를 동적으로 생성하고 AspectRatioFitter를 부착합니다.
    /// ConfigManager.DisplayFitMode 설정값에 따라 Stretch / Fit Outside / Fit Inside를 적용합니다.
    /// </summary>
    private RawImage CreateFullscreenRawImage(GameObject parent, string name)
    {
        GameObject imgObj = new GameObject(name);
        imgObj.transform.SetParent(parent.transform, false);

        // RectTransform을 전체화면으로 스트레치
        RectTransform rt = imgObj.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // 검정 배경 패널 추가 (fit_inside 시 빈 영역에 이전 영상이 비치는 것 방지)
        UnityEngine.UI.Image bg = imgObj.AddComponent<UnityEngine.UI.Image>();
        bg.color = Color.black;

        // 콘텐츠 표시용 RawImage (배경 위에 자식으로 생성)
        GameObject rawObj = new GameObject(name + "_Content");
        rawObj.transform.SetParent(imgObj.transform, false);
        RectTransform rawRt = rawObj.AddComponent<RectTransform>();
        rawRt.anchorMin = Vector2.zero;
        rawRt.anchorMax = Vector2.one;
        rawRt.offsetMin = Vector2.zero;
        rawRt.offsetMax = Vector2.zero;

        RawImage rawImage = rawObj.AddComponent<RawImage>();

        // Display_Fit_Mode 설정에 따라 AspectRatioFitter 구성
        AspectRatioFitter fitter = rawObj.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = ConfigManager.DisplayFitMode switch
        {
            "stretch" => AspectRatioFitter.AspectMode.None,
            "fit_outside" => AspectRatioFitter.AspectMode.EnvelopeParent,
            "fit_inside" => AspectRatioFitter.AspectMode.FitInParent,
            _ => AspectRatioFitter.AspectMode.EnvelopeParent  // 기본값: fit_outside
        };

        return rawImage;
    }
}
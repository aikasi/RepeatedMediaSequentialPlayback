using System;
using UnityEngine;
// AVPro Video 사용 선언
using RenderHeads.Media.AVProVideo;

[RequireComponent(typeof(MediaPlayer))]
public class VideoWatchdog : MonoBehaviour
{
    private MediaPlayer _mediaPlayer;
    private double _lastTime;
    private float _stallTimer;
    private int _stallCount; // 연속으로 복구 시도(Play)를 한 횟수


    /// <summary>
    /// 치명적 오류 발생 시 (재생 불능 상태) 외부 시스템(PlaybackManager 등)으로 알리는 이벤트
    /// </summary>
    public event Action<MediaPlayer> OnFatalError;

    private void Awake()
    {
        _mediaPlayer = GetComponent<MediaPlayer>();
    }

    private void OnEnable()
    {
        _stallTimer = 0f;
        _stallCount = 0;
        _lastTime = -1.0;
    }

    private void Update()
    {
        // 1. 영상이 준비되지 않았거나 컨트롤러가 없으면 감시 패스
        if (_mediaPlayer == null || _mediaPlayer.Control == null || !_mediaPlayer.Control.CanPlay())
            return;

        // 2. [Edge Case 방어] 현재 미디어가 영상이 아닌 이미지(사진)일 경우 시간이 흐르지 않으므로 감시 패스
        // AVPro에서 이미지가 로드된 경우 보통 Duration이 0이거나 매우 작게 잡히는 것을 판별
        if (Mathf.Approximately((float)_mediaPlayer.Info.GetDuration(), 0f))
        {
            return; // 이미지이므로 프리징 감시에서 뺌
        }

        // 3. PlaybackManager 등에 의해 정상적으로 '재생 지시'를 받은 상태인지 확인
        if (_mediaPlayer.Control.IsPlaying())
        {
            CheckForStalls();
        }
    }

    private void CheckForStalls()
    {
        double currentTime = _mediaPlayer.Control.GetCurrentTime();

        // 재생이 끝난 상태(완료)라면 감시 중단
        if (_mediaPlayer.Control.IsFinished()) 
            return;

        // 아주 미세한 오차 보정을 위해 Float 입실론 사용
        if (Mathf.Approximately((float)currentTime, (float)_lastTime))
        {
            _stallTimer += Time.deltaTime;

            if (_stallTimer >= ConfigManager.StallThreshold)
            {
                RecoverVideo();
            }
        }
        else
        {
            // 시간이 정상적으로 흐르고 있다면 스톨 타이머와 카운트 모두 초기화 (자가 치유 성공)
            _lastTime = currentTime;
            _stallTimer = 0f;
            if (_stallCount > 0)
            {
                if (Logger.Instance != null)
                    Logger.Instance.Enqueue($"[Watchdog] 미디어 멈춤 복구 성공: {_mediaPlayer.MediaPath.Path}");
            }
            _stallCount = 0;
        }
    }

    private void RecoverVideo()
    {
        _stallTimer = 0f;
        _stallCount++;

        if (_stallCount > ConfigManager.MaxStallRetries)
        {
            // 더 이상 복구가 불가능한 상태(Hard Recovery 필요)
            if (Logger.Instance != null)
                Logger.Instance.Enqueue($"[Watchdog] 치명적 오류 - 연속 멈춤 복구 불가 ({ConfigManager.MaxStallRetries}회 초과): {_mediaPlayer.MediaPath.Path}");

            // 이벤트를 통해 PlaybackManager에게 시스템 전체 0번 리셋을 요청
            OnFatalError?.Invoke(_mediaPlayer);
            
            // 이벤트 전파 후 더 이상 스스로 발악하지 않도록 감시 일시 중단(쿨타임 역할)
            _stallCount = -9999; 
            return;
        }

        // 얕은 복구 (Soft Recovery): 플레이어 엔진에 강제로 다시 Play() 트리거 (오작동에 의한 일시 정지 풀기)
        if (Logger.Instance != null)
            Logger.Instance.Enqueue($"[Watchdog] 미디어 멈춤 감지! 강제 Play 시도 ({_stallCount}/{ConfigManager.MaxStallRetries}): {_mediaPlayer.MediaPath.Path}");
            
        _mediaPlayer.Play();
    }
}
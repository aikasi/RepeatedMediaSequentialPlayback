using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using RenderHeads.Media.AVProVideo;

/// <summary>
/// 모니터 1개를 담당하는 렌더링 컴포넌트.
/// AVPro MediaPlayer의 텍스처를 RawImage에 매 프레임 갱신하고,
/// 더블 버퍼 전환 시 Alpha 크로스페이드를 수행합니다.
/// PlaybackManager에 의해 런타임에 생성되고 초기화됩니다.
/// </summary>
public class DisplayRenderer : MonoBehaviour
{
    // 더블 버퍼 역할의 RawImage 두 개
    private RawImage _imageA;
    private RawImage _imageB;

    // 각 RawImage에 연결된 AVPro MediaPlayer 참조
    private MediaPlayer _playerA;
    private MediaPlayer _playerB;

    // CanvasGroup은 Alpha 페이드에 사용됩니다 (RawImage의 color.a보다 일괄 처리에 효율적)
    private CanvasGroup _groupA;
    private CanvasGroup _groupB;

    // 현재 활성화된 버퍼 인덱스 (0=A, 1=B)
    private int _activeBuffer = 0;

    // 크로스페이드 진행 중 여부 플래그 (중복 실행 방지)
    private bool _isFading = false;

    // 해당 모니터 전용 에러 오버레이 컨트롤러 (프리팹 기반)
    private ErrorOverlayController _errorOverlay;

    /// <summary>
    /// PlaybackManager가 런타임에 호출하는 초기화 메서드.
    /// RawImage, CanvasGroup, MediaPlayer, 그리고 에러 오버레이 참조를 주입합니다.
    /// </summary>
    public void Initialize(RawImage imageA, RawImage imageB, MediaPlayer playerA, MediaPlayer playerB, ErrorOverlayController errorOverlay)
    {
        _imageA = imageA;
        _imageB = imageB;
        _playerA = playerA;
        _playerB = playerB;
        _errorOverlay = errorOverlay;

        // CanvasGroup은 부모(검정 배경 포함)에 부착해야 크로스페이드가 배경+콘텐츠 모두 제어
        _groupA = imageA.transform.parent.gameObject.GetOrAddComponent<CanvasGroup>();
        _groupB = imageB.transform.parent.gameObject.GetOrAddComponent<CanvasGroup>();

        // 초기 상태: A는 완전히 보이고 B는 완전히 숨김
        _groupA.alpha = 1f;
        _groupB.alpha = 0f;
        _activeBuffer = 0;

        Debug.Log($"[DisplayRenderer] 초기화 완료 - {gameObject.name}");
    }

    private void Update()
    {
        // 매 프레임 각 버퍼의 최신 텍스처를 RawImage에 반영
        UpdateTexture(_imageA, _playerA);
        UpdateTexture(_imageB, _playerB);
    }

    /// <summary>
    /// AVPro TextureProducer에서 텍스처를 가져와 RawImage에 적용합니다.
    /// 텍스처가 없거나 변경이 없으면 업데이트를 스킵합니다.
    /// </summary>
    private void UpdateTexture(RawImage rawImage, MediaPlayer player)
    {
        if (rawImage == null || player == null || player.TextureProducer == null)
            return;

        Texture texture = player.TextureProducer.GetTexture();
        if (texture != null && rawImage.texture != texture)
        {
            rawImage.texture = texture;

            // AVPro는 플랫폼에 따라 텍스처가 수직 반전될 수 있으므로 UV 보정
            bool requiresFlip = player.TextureProducer.RequiresVerticalFlip();
            rawImage.uvRect = new Rect(0f, requiresFlip ? 1f : 0f, 1f, requiresFlip ? -1f : 1f);

            // 영상 텍스처 갱신 시 AspectRatioFitter를 영상용 모드로 복원 (이미지 설정 오염 방지)
            AspectRatioFitter fitter = rawImage.GetComponent<AspectRatioFitter>();
            if (fitter != null && texture.width > 0 && texture.height > 0)
            {
                fitter.aspectRatio = (float)texture.width / texture.height;
                fitter.aspectMode = ConfigManager.DisplayFitMode switch
                {
                    "stretch"      => AspectRatioFitter.AspectMode.None,
                    "fit_outside"  => AspectRatioFitter.AspectMode.EnvelopeParent,
                    "fit_inside"   => AspectRatioFitter.AspectMode.FitInParent,
                    _              => AspectRatioFitter.AspectMode.EnvelopeParent
                };
            }
        }
    }

    /// <summary>
    /// 대상 버퍼로 Alpha 크로스페이드 전환을 수행합니다.
    /// PlaybackManager의 ExecuteCrossfadeAndSwap에서 호출됩니다.
    /// </summary>
    public IEnumerator CrossfadeTo(int targetBufferIndex, float duration)
    {
        // 이미 페이드 중이거나 동일한 버퍼 요청은 무시
        if (_isFading || targetBufferIndex == _activeBuffer) yield break;
        _isFading = true;

        // 전환 대상 그룹 결정
        CanvasGroup fadeOut = targetBufferIndex == 1 ? _groupA : _groupB;
        CanvasGroup fadeIn  = targetBufferIndex == 1 ? _groupB : _groupA;

        // 안전하게 시작 값 초기화
        fadeOut.alpha = 1f;
        fadeIn.alpha  = 0f;

        // Alpha 트위닝: 선형 보간으로 부드럽게 전환
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            fadeIn.alpha  = t;
            fadeOut.alpha = 1f - t;
            yield return null;
        }

        // 최종값 고정 (부동소수점 오차 방지)
        fadeIn.alpha  = 1f;
        fadeOut.alpha = 0f;

        _activeBuffer = targetBufferIndex;
        _isFading = false;
    }

    /// <summary>
    /// 치명적 오류 등으로 인한 강제 리셋 시, 진행 중인 크로스페이드를 즉시 중단하고
    /// 지정된 버퍼(보통 0번)가 강제로 화면에 즉시 표시되도록 스냅(Snap) 합니다.
    /// </summary>
    public void ForceResetToBuffer(int targetBufferIndex)
    {
        // 1. 진행 중인 페이드 코루틴 강제 종료
        StopAllCoroutines();
        _isFading = false;
        
        // 2. 대상 버퍼는 즉시 100% 알파, 다른 하나는 0% 알파로 고정
        if (targetBufferIndex == 0)
        {
            _groupA.alpha = 1f;
            _groupB.alpha = 0f;
        }
        else
        {
            _groupA.alpha = 0f;
            _groupB.alpha = 1f;
        }

        _activeBuffer = targetBufferIndex;
        Debug.LogWarning($"[DisplayRenderer] 화면 강제 전환 스냅 완료: 대상 버퍼 {targetBufferIndex}");
    }

    /// <summary>
    /// 이미지 텍스처를 직접 RawImage에 설정합니다 (AVPro는 이미지 코덱을 지원하지 않으므로 우회).
    /// 이전 이미지 텍스처가 있으면 메모리 누수 방지를 위해 자동 해제합니다.
    /// </summary>
    public void SetImageTexture(int bufferIndex, Texture2D texture)
    {
        RawImage target = bufferIndex == 0 ? _imageA : _imageB;

        // 이전 이미지 텍스처 메모리 해제 (VRAM 누수 방지)
        if (target.texture != null && target.texture is Texture2D oldTex)
        {
            Destroy(oldTex);
        }

        target.texture = texture;
        target.uvRect = new Rect(0, 0, 1, 1);  // 이미지는 UV 반전 불필요

        // 이미지 전용 화면 맞춤 모드 및 비율 적용 (영상과 별도 설정)
        AspectRatioFitter fitter = target.GetComponent<AspectRatioFitter>();
        if (fitter != null && texture.width > 0 && texture.height > 0)
        {
            fitter.aspectRatio = (float)texture.width / texture.height;
            fitter.aspectMode = ConfigManager.ImageFitMode switch
            {
                "stretch"      => AspectRatioFitter.AspectMode.None,
                "fit_outside"  => AspectRatioFitter.AspectMode.EnvelopeParent,
                "fit_inside"   => AspectRatioFitter.AspectMode.FitInParent,
                _              => AspectRatioFitter.AspectMode.EnvelopeParent
            };
        }
    }

    /// <summary>
    /// 영상 로딩 시 AspectRatioFitter를 영상용 모드로 사전 복원합니다.
    /// 이전 이미지의 fit 설정이 영상에 오염되는 것을 방지합니다.
    /// </summary>
    public void RestoreVideoFitMode(int bufferIndex)
    {
        RawImage target = bufferIndex == 0 ? _imageA : _imageB;
        AspectRatioFitter fitter = target.GetComponent<AspectRatioFitter>();
        if (fitter != null)
        {
            fitter.aspectMode = ConfigManager.DisplayFitMode switch
            {
                "stretch"      => AspectRatioFitter.AspectMode.None,
                "fit_outside"  => AspectRatioFitter.AspectMode.EnvelopeParent,
                "fit_inside"   => AspectRatioFitter.AspectMode.FitInParent,
                _              => AspectRatioFitter.AspectMode.EnvelopeParent
            };
        }
    }

    /// <summary>
    /// 깨진 미디어 감지 시, 해당 모니터 화면 전체에 빨간 경고 텍스트를 표시합니다.
    /// 버퍼 종속성이 없으므로 모니터당 1개의 오버레이만 사용합니다.
    /// </summary>
    public void ShowErrorOverlay(string message)
    {
        if (_errorOverlay != null)
        {
            _errorOverlay.ShowError(message);
        }
    }

    /// <summary>
    /// 정상 미디어 로딩 시, 모니터의 경고 텍스트 오버레이를 숨깁니다.
    /// </summary>
    public void HideErrorOverlay()
    {
        if (_errorOverlay != null)
        {
            _errorOverlay.HideError();
        }
    }
}

/// <summary>
/// GetOrAddComponent 확장 메서드 - CanvasGroup 자동 부착 헬퍼
/// </summary>
public static class GameObjectExtensions
{
    public static T GetOrAddComponent<T>(this GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        if (component == null)
            component = go.AddComponent<T>();
        return component;
    }
}

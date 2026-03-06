using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 깨진 미디어(이미지/영상) 발견 시 화면에 경고 메시지를 표시하는 단일 책임 컴포넌트.
/// UI 요소는 Unity 에디터에서 직접 생성한 뒤 Inspector에서 연결합니다.
/// </summary>
public class ErrorOverlayController : MonoBehaviour
{
    public static ErrorOverlayController Instance { get; private set; }

    [Header("UI 연결 (에디터에서 드래그)")]
    [Tooltip("경고 메시지를 감싸는 반투명 배경 패널 (에러 시 활성화됨)")]
    [SerializeField] private GameObject _overlayPanel;

    [Tooltip("에러 내용을 표시할 텍스트 컴포넌트")]
    [SerializeField] private TextMeshProUGUI _errorText;

    [Tooltip("경고창을 수동으로 닫는 버튼 (선택사항)")]
    [SerializeField] private Button _closeButton;

    private void Awake()
    {
        // 싱글턴 패턴: 씬에 하나만 존재하도록 보장
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        // 닫기 버튼이 연결되어 있으면 클릭 이벤트 자동 바인딩
        if (_closeButton != null)
        {
            _closeButton.onClick.AddListener(HideError);
        }

        // 초기 상태: 숨김
        if (_overlayPanel != null)
        {
            _overlayPanel.SetActive(false);
        }
    }

    /// <summary>
    /// 에러 메시지를 화면에 표시합니다.
    /// 이미 표시 중이면 메시지만 갱신합니다.
    /// </summary>
    public void ShowError(string message)
    {
        if (_overlayPanel != null)
        {
            _overlayPanel.SetActive(true);
        }

        if (_errorText != null)
        {
            _errorText.text = message;
        }

        Debug.LogWarning($"[ErrorOverlay] 화면 경고 표시: {message}");
    }

    /// <summary>
    /// 경고 오버레이를 숨깁니다.
    /// 닫기 버튼 클릭 또는 정상 재생 복구 시 호출됩니다.
    /// </summary>
    public void HideError()
    {
        if (_overlayPanel != null)
        {
            _overlayPanel.SetActive(false);
        }

        if (_errorText != null)
        {
            _errorText.text = "";
        }
    }
}

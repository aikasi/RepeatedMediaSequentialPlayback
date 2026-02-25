using UnityEngine;

/// <summary>
/// 미디어 파일의 구분을 위한 열거형.
/// </summary>
public enum MediaType
{
    Unknown,
    Video,
    Image
}

/// <summary>
/// 미디어 파일의 순서(Index), 타입, 절대경로, 재생시간 등 데이터를 담는 순수 컨테이너 클래스
/// (사용자 제약 7번 단일 기능 준수: 데이터 구조체 역할만 담당)
/// </summary>
[System.Serializable]
public class MediaData
{
    /// <summary>
    /// 재생 순서 정렬용 인덱스 (파일명 앞부분에서 정규식 파싱 시 자동 할당, 예: 파일명이 "02_XX.mp4" 라면 2)
    /// </summary>
    public int Index;

    /// <summary>
    /// 확장자를 통해 판별된 미디어의 종류 (비디오 인지 이미지 인지)
    /// </summary>
    public MediaType Type;

    /// <summary>
    /// 파일의 절대 경로 (재생 시 폴더 탐색 I/O 비용 및 병목을 줄이기 위해 캐싱 된 데이터)
    /// </summary>
    public string FilePath;

    /// <summary>
    /// 재생 유지 시간 (파일명에서 파싱됨, 옵션). 영상일 경우 이 값보다 영상 고유의 길이를 우선하도록 엔진 쪽에서 처리 추천.
    /// </summary>
    public float Duration;
}

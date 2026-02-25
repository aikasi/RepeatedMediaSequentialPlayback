using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// 로컬 폴더(StreamingAssets)를 탐색하여 조건에 맞는 미디어 파일의 절대경로를 캐싱하고 
/// 빠른 재생을 위한 플레이리스트 자료구조를 생성하는 스크립트.
/// </summary>
public class MediaScanner : MonoBehaviour
{
    /// <summary>
    /// 모니터 인덱스(Display Index)를 Key로 하고, 해당 모니터에서 재생할 MediaData 리스트를 Value로 갖는 최적화 파싱 결과
    /// </summary>
    public Dictionary<int, List<MediaData>> Playlist { get; private set; } = new Dictionary<int, List<MediaData>>();

    /// <summary>
    /// 파일 번호(Index) 기반 O(1) 조회용 딕셔너리. PlaylistByIndex[모니터번호][파일번호] = MediaData
    /// </summary>
    public Dictionary<int, Dictionary<int, MediaData>> PlaylistByIndex { get; private set; } = new Dictionary<int, Dictionary<int, MediaData>>();

    /// <summary>
    /// 모든 모니터에 공통으로 존재하는 최대 연속 파일 번호 (시작 시 1회 계산)
    /// 예: Media0=[01,02,03,06], Media1=[01,02,03] → MaxValidIndex = 3
    /// </summary>
    public int MaxValidIndex { get; private set; } = 0;

    // 파일 정규화 파싱 정규식
    // 그룹1: 인덱스 (\d+), 그룹2(옵션): 시간 (\d+ 또는 \d+.\d+), 그룹3: 미디어 확장자
    // 예: "01_10.mp4" -> Index 1, Duration 10, Type mp4
    // 예: "01-6.png"  -> Index 1, Duration 6, Type png
    // 예: "02.jpg"    -> Index 2, Duration X, Type jpg
    private readonly Regex filePattern = new Regex(@"^(\d+)[_\-]?(\d+(?:\.\d+)?)?.*\.([a-zA-Z0-9]+)$");

    private void Awake()
    {
        ScanMediaFiles();
        BuildIndexDictionary();
        PerformDiagnosticAudit(); // [NEW] 2단계 정밀 누락 감사 추가
        CalculateMaxValidIndex();
    }

    /// <summary>
    /// 메인 모니터(Media0)를 기준으로 모든 파일 누락(이빨 빠짐)을 정밀 진단하여 초기 로그로 박제합니다.
    /// 실제 재생 길이에는 영향을 주지 않으며 순수 관리자 알림(보고) 용도입니다.
    /// </summary>
    private void PerformDiagnosticAudit()
    {
        if (!PlaylistByIndex.ContainsKey(0) || PlaylistByIndex[0].Count == 0)
        {
            Debug.LogWarning("[MediaScanner] [진단오류] 메인 모니터(Media0)에 파일이 하나도 없습니다!");
            return;
        }

        // 메인 모니터(Media0)의 파일 인덱스들을 가져옴
        var media0Dict = PlaylistByIndex[0];
        
        // 메인 모니터에 있는 가장 큰 파일 번호 찾기
        int maxIndexInMedia0 = 0;
        foreach (int key in media0Dict.Keys)
        {
            if (key > maxIndexInMedia0) maxIndexInMedia0 = key;
        }

        if (maxIndexInMedia0 == 0) return;

        Debug.Log($"[MediaScanner] ----- ⚠️ 미디어 파일 누락 진단 시작 (메인 최대 번호: {maxIndexInMedia0}) -----");

        // [1단계] 메인 모니터(Media0) 자체의 연속성 검사 (1번부터 max 번호까지)
        // 만약 Media0 자체에 4번, 6번 등이 없다면 먼저 경고를 띄움
        for (int idx = 1; idx <= maxIndexInMedia0; idx++)
        {
            if (!media0Dict.ContainsKey(idx))
            {
                Debug.LogWarning($"[MediaScanner] [Media0 (메인)] 누락 파일 발견: {idx:D2}번 파일이 없습니다!");
            }
        }

        // [2단계] 서브 모니터들을 메인 모니터에 "실제로 존재하는 번호들"과 1:1 대조
        for (int monitorId = 1; monitorId < Display.displays.Length; monitorId++)
        {
            if (!PlaylistByIndex.ContainsKey(monitorId))
            {
                Debug.LogWarning($"[MediaScanner] [Media{monitorId} (서브)] 폴더 전체 누락: 파일이 0개입니다!");
                continue;
            }

            var subMonitorDict = PlaylistByIndex[monitorId];

            // 메인 모니터가 가지고 있는 번호들을 순회하며 서브 모니터도 가지고 있는지 확인
            foreach (int validIdx in media0Dict.Keys)
            {
                if (!subMonitorDict.ContainsKey(validIdx))
                {
                    Debug.LogWarning($"[MediaScanner] [Media{monitorId} (서브)] 누락 파일 발견: 메인에는 있는 {validIdx:D2}번 파일이 없습니다!");
                }
            }
        }
        
        Debug.Log($"[MediaScanner] ---------------------------------------------");
    }

    /// <summary>
    /// 지정된 폴더 구조에 맞게 미디어 파일을 스캔하고 플레이리스트 딕셔너리를 초기 1회만 구축합니다.
    /// </summary>
    private void ScanMediaFiles()
    {
        string baseMediaPath = Application.streamingAssetsPath;
        
        // 연결된 모니터 수만큼 순회하며 "Media0", "Media1" 폴더 스캔
        for (int i = 0; i < Display.displays.Length; i++)
        {
            string mediaFolderPath = Path.Combine(baseMediaPath, $"Media{i}");
            
            // 필수 폴더 누락 등의 예외 환경(Edge Case) 대응
            if (!Directory.Exists(mediaFolderPath))
            {
                Debug.LogWarning($"[MediaScanner] 미디어 폴더 누락 확인: {mediaFolderPath} (비어있음 처리)");
                continue;
            }

            List<MediaData> mediaList = new List<MediaData>();
            string[] files = Directory.GetFiles(mediaFolderPath);

            foreach (string filePath in files)
            {
                // Unity 내부에서 무작위로 생성하는 .meta 파일 등은 스캔에서 과감히 무시
                if (filePath.EndsWith(".meta")) continue; 

                string fileName = Path.GetFileName(filePath);
                Match match = filePattern.Match(fileName);

                if (match.Success)
                {
                    MediaData newMedia = new MediaData();
                    newMedia.FilePath = filePath.Replace("\\", "/"); // 경로 규격화 및 슬래시 정리

                    // 1. 순서(Index) 안전 파싱
                    if (int.TryParse(match.Groups[1].Value, out int index))
                    {
                        newMedia.Index = index;
                    }

                    // 2. 재생 시간(Duration) 안전 파싱
                    if (match.Groups[2].Success && float.TryParse(match.Groups[2].Value, out float duration))
                    {
                        newMedia.Duration = duration;
                    }

                    // 3. 미디어 타입(Type) 판별
                    string extension = match.Groups[3].Value.ToLower();
                    newMedia.Type = DetermineMediaType(extension);

                    if (newMedia.Type != MediaType.Unknown)
                    {
                        mediaList.Add(newMedia);
                    }
                    else
                    {
                        // 확장자 제약 조건 위반 감지
                        Debug.LogWarning($"[MediaScanner] 지원하지 않는 미디어 포맷 감지 및 제외: {fileName}");
                    }
                }
            }

            // 파싱 완료된 데이터 리스트를 인덱스 번호를 기준으로 오름차순 시퀀스 정렬
            mediaList.Sort((a, b) => a.Index.CompareTo(b.Index));
            Playlist.Add(i, mediaList);

            Debug.Log($"[MediaScanner] 모니터 [{i}] 플레이리스트 캐싱 완료 (총 검색된 올바른 미디어 갯수: {mediaList.Count}개)");
        }
    }

    /// <summary>
    /// List 기반 Playlist를 파일번호(Index) 기반 Dictionary로 변환합니다 (O(1) 조회용).
    /// </summary>
    private void BuildIndexDictionary()
    {
        foreach (var pair in Playlist)
        {
            var dict = new Dictionary<int, MediaData>();
            foreach (var media in pair.Value)
            {
                dict[media.Index] = media;
            }
            PlaylistByIndex[pair.Key] = dict;
        }
    }

    /// <summary>
    /// 01번부터 시작하여 모든 모니터에 공통으로 존재하는 최대 연속 파일 번호를 계산합니다.
    /// </summary>
    private void CalculateMaxValidIndex()
    {
        if (PlaylistByIndex.Count == 0)
        {
            MaxValidIndex = 0;
            Debug.LogWarning("[MediaScanner] 플레이리스트가 비어있어 MaxValidIndex = 0");
            return;
        }

        int index = 1;
        while (true)
        {
            bool allHave = true;
            foreach (var pair in PlaylistByIndex)
            {
                if (!pair.Value.ContainsKey(index))
                {
                    allHave = false;
                    break;
                }
            }
            if (!allHave) break;
            index++;
        }
        MaxValidIndex = index - 1;
        Debug.LogWarning($"[MediaScanner] 모든 모니터 공통 연속 범위: 01 ~ {MaxValidIndex:D2} (이후 번호는 리셋됨)");
    }

    /// <summary>
    /// 확장자를 입력받아 외부 설정(ConfigManager) 기준으로 비디오인지 이미지인지 시스템에서 규격화하는 단위로 판별합니다.
    /// </summary>
    private MediaType DetermineMediaType(string extension)
    {
        // Array.Exists를 사용하여 설정된 확장자 배열에 포함되어 있는지 검사합니다.
        if (System.Array.Exists(ConfigManager.AllowedVideoExtensions, ext => ext == extension))
        {
            return MediaType.Video;
        }
        else if (System.Array.Exists(ConfigManager.AllowedImageExtensions, ext => ext == extension))
        {
            return MediaType.Image;
        }
        
        return MediaType.Unknown;
    }
}

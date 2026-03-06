# Repeated Media Sequential Playback

다중 모니터 환경에서 미디어 파일(영상/이미지)을 **순차적으로 반복 재생**하는 Unity 기반 전시용 미디어 플레이어입니다.

## 주요 기능

- **다중 모니터 동기화 재생** — 메인 모니터(마스터) 기준으로 영상 전환 시점을 동기화
- **더블 버퍼링** — 백그라운드에서 다음 영상을 미리 로드하여 끊김 없는 전환
- **크로스페이드 전환** — 영상 간 부드러운 알파 트랜지션
- **자동 에러 복구** — 영상 프리징 감지(Watchdog) 및 파일 로딩 실패 시 자동 복구
- **영상 + 이미지 혼합 재생** — 동일 시퀀스에서 영상과 이미지를 번갈아 재생 가능
- **외부 설정 파일** — `Settings.txt`로 해상도, 타이머, 확장자 등 런타임 조정

---

## 폴더 구조

```
프로젝트 루트/
├── Settings.txt                    # 외부 설정 파일 (빌드 시 exe 옆에 배치)
└── Assets/
    ├── Settings.txt                # 에디터용 설정 파일
    ├── StreamingAssets/
    │   ├── Media0/                 # 메인 모니터(0번) 미디어 폴더
    │   │   ├── 01.mp4
    │   │   ├── 02_10.png           # 02번, 10초 표시
    │   │   └── 03.mp4
    │   ├── Media1/                 # 서브 모니터(1번) 미디어 폴더
    │   │   ├── 01.mp4
    │   │   ├── 02_10.png
    │   │   └── 03.mp4
    │   └── Media2/                 # 서브 모니터(2번) - 필요 시 추가
    └── Scripts/
        ├── Core/
        │   ├── ConfigManager.cs    # 설정 관리 및 디스플레이 초기화
        │   ├── PlaybackManager.cs  # 재생 동기화, 버퍼링, 에러 복구 통괄
        │   └── VideoWatchdog.cs    # 영상 프리징 감지 및 자동 복구
        ├── Data/
        │   ├── MediaData.cs        # 미디어 데이터 컨테이너
        │   └── MediaScanner.cs     # 미디어 폴더 스캔 및 플레이리스트 생성
        ├── UI/
        │   └── DisplayRenderer.cs  # 모니터별 렌더링 및 크로스페이드
        └── Utility/
            ├── CSVReader.cs        # CSV/설정 파일 파서
            └── Logger.cs           # 비동기 로그 파일 기록기
```

---

## 미디어 파일 네이밍 규칙

```
{순서번호}[_{재생시간}].{확장자}
```

| 파일명 예시    | 순서 | 재생 시간      | 타입   |
| -------------- | ---- | -------------- | ------ |
| `01.mp4`     | 1번  | 영상 자체 길이 | 영상   |
| `02_10.png`  | 2번  | 10초           | 이미지 |
| `03-5.5.jpg` | 3번  | 5.5초          | 이미지 |
| `04.mov`     | 4번  | 영상 자체 길이 | 영상   |

- 순서번호는 01부터 시작하며, 모든 모니터에 공통으로 존재하는 연속 번호까지만 재생됩니다.
- 구분자는 언더스코어(`_`) 또는 하이픈(`-`) 모두 지원합니다.

---

## Settings.txt 설정 항목

| Key                     | 기본값                   | 설명                                                              |
| ----------------------- | ------------------------ | ----------------------------------------------------------------- |
| `Display0_Resolution` | 1920x1080                | 메인 모니터 해상도 (예:`1920x1080`)                             |
| `Display1_Resolution` | 1920x1080                | 서브 모니터 1 해상도                                              |
| `Crossfade_Delay`     | `1.0`                  | 화면 전환 페이드 시간 (초)                                        |
| `Sync_Timeout`        | `3.0`                  | 다음 버퍼 준비 최대 대기 시간 (초)                                |
| `Async_Load_Delay`    | `0.2`                  | 모니터 간 로딩 분산 딜레이 (초)                                   |
| `Stall_Threshold`     | `1.0`                  | 영상 프리징 판단 기준 시간 (초)                                   |
| `Max_Stall_Retries`   | `3`                    | 프리징 자동 복구 최대 시도 횟수                                   |
| `Display_Fit_Mode`    | `fit_outside`          | 영상 화면 맞춤 (`stretch` / `fit_outside` / `fit_inside`)   |
| `Image_Fit_Mode`      | `fit_outside`          | 이미지 화면 맞춤 (`stretch` / `fit_outside` / `fit_inside`) |
| `Mute_Audio`          | `false`                | 동영상 음소거 (`true` / `false`)                              |
| `Video_Extensions`    | `mp4;mov;webm;mkv;avi` | 인식할 영상 확장자 (세미콜론 구분)                                |
| `Image_Extensions`    | `jpg;jpeg;png`         | 인식할 이미지 확장자 (세미콜론 구분)                              |

---

## 로그 파일 위치

실행 중 발생하는 에러, 경고, 복구 이력이 자동으로 기록됩니다.

```
C:\Users\{사용자명}\AppData\LocalLow\MetaDevs\RepeatedMediaSequentialPlayback\Logs\
```

- 앱 실행 시마다 `yyMMdd-HHmmss.log` 형식으로 새 파일이 생성됩니다.
- 최대 1,000개까지 보관되며, 초과 시 가장 오래된 파일부터 자동 삭제됩니다.

---

## 에러 복구 동작

### 영상 프리징 (Watchdog)

1. 재생 시간이 `Stall_Threshold`초 이상 멈추면 자동으로 `Play()` 재시도
2. `Max_Stall_Retries`회 초과 시 → **시스템 전체 01번 리셋**

### 파일 로딩 실패 (AVPro 네이티브 에러)

- **활성 화면 에러** → 즉시 시스템 전체 01번 리셋
- **백그라운드 에러** → 현재 화면 유지, 백그라운드 버퍼에 01번 대체 로딩

---

## 디버그

| 키 입력      | 동작                    |
| ------------ | ----------------------- |
| `Ctrl + 1` | 다음 시퀀스로 강제 전환 |

---

## 빌드 및 배포

1. Unity에서 빌드
2. 빌드 폴더에 `Settings.txt` 배치
3. 빌드 폴더 내 `{앱이름}_Data/StreamingAssets/` 아래에 `Media0/`, `Media1/` 등 미디어 폴더 배치
4. 실행

---

## 기술 사양

- **Unity 버전:** 6000.3.9f1
- **미디어 엔진:** AVPro Video
- **지원 이미지:** JPG, JPEG, PNG (BMP 미지원 — Unity 네이티브 제한)
- **지원 영상:** MP4, MOV, WebM, MKV, AVI

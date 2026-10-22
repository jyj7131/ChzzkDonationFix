# ChzzkDonationFix

XSplit에서 치지직 영상도네이션(유튜브)이 연달아 재생될 때 발생하는
**"현재 요금제 기준으로 너무 많은 기기를 스트리밍 중이므로 재생이 일시중지되었습니다"** 오류를 해결하는 Windows용 런처.

XSplit 내장 브라우저에 유튜브 기기 식별 쿠키를 넣어 준 뒤 XSplit을 실행하고 종료한다.

## 증상

- 대상: XSplit Broadcaster 4.6 (내장 CEF = Chrome 103), 치지직 영상도네이션 `cookie=true` 모드(유튜브 프리미엄 로그인 상태로 재생)
- 음원이 포함된 영상이 연달아 재생되면 두 번째 영상부터 위 문구와 함께 재생이 멈춘다.
- 같은 조건에서 OBS(CEF = Chrome 127)는 발생하지 않는다.

## 원인

### 1. 오류는 프리미엄 동시 재생 확인(heartbeat) 응답이다

임베드 플레이어는 재생 시작 후 `POST /youtubei/v1/player/heartbeat` (`HEARTBEAT_CHECK_TYPE_YPC`)를 보낸다.
XSplit에서 두 번째 영상의 heartbeat 응답이 `playabilityStatus.status = UNPLAYABLE`과 위 문구를 담고 있었다.

OBS도 같은 heartbeat를 보내며, 응답은 항상 `OK`였다. (영상도네이션 소스 3개가 같은 영상을 동시에 재생한 경우에도 `OK`)

### 2. 차이는 기기 식별값(visitor ID)이다

| | XSplit | OBS |
|---|---|---|
| `X-Goog-Visitor-Id` | **임베드가 바뀔 때마다 새 값** | 모든 임베드에서 동일 |
| 두 번째 영상 heartbeat | `UNPLAYABLE` | `OK` |

유튜브는 visitor ID로 기기를 구분한다. XSplit에서는 영상도네이션 하나하나가 다른 기기로 인식되어,
직전 영상의 기기가 아직 재생 중으로 남아 있는 상태에서 새 기기가 재생을 시작하므로 동시 재생 제한에 걸린다.

직전 영상이 끝나고(`watchtime final=1`) 4~6초 뒤의 heartbeat에서도 차단되었다.
임베드 플레이어는 종료 시 동시 재생 기록을 즉시 정리하는 신호를 보내지 않는 것으로 보인다(추정).
실제 기기 간 전환(앱 → 다른 기기)에서는 같은 문제가 재현되지 않았다.

### 3. XSplit이 visitor 쿠키를 저장하지 못한다

유튜브는 visitor 쿠키를 `Partitioned`(CHIPS) 속성으로 발급한다.

```
Set-Cookie: VISITOR_INFO1_LIVE=...; Domain=.youtube.com; Secure; HttpOnly; SameSite=none; Partitioned
Set-Cookie: VISITOR_PRIVACY_METADATA=...; ... SameSite=none; Partitioned
Set-Cookie: __Secure-ROLLOUT_TOKEN=...; ... SameSite=none; Partitioned
Set-Cookie: __Secure-YNID=...; ... SameSite=none; Partitioned
```

CHIPS는 Chrome 114부터 정식 지원된다. XSplit의 Chrome 103은 이 쿠키를 **차단 사유도 남기지 않고 버린다**
(DevTools `responseReceivedExtraInfo.blockedCookies`에 없음, 쿠키 저장소에도 없음).
같은 응답의 `Partitioned`가 없는 쿠키(`__Secure-3PSIDCC` 등)는 정상 저장된다.

그 결과 임베드마다 visitor 쿠키가 없는 상태로 요청하게 되고, 유튜브가 매번 새 visitor ID를 발급한다.

### 검증

- visitor 쿠키를 `Partitioned` 없이 XSplit 쿠키 저장소에 넣자 visitor ID가 고정되고, heartbeat가 그대로 전송되는 상태에서 두 번째 영상도 `OK`로 끝까지 재생되었다.
- XSplit 재시작 후에도 쿠키가 유지되었다.
- 확인된 사항이 아닌 가설은 위에 "추정"으로 표시했다.

## 해결 방식

유튜브에서 visitor 쿠키를 새로 발급받아, `Partitioned` 속성만 빼고 XSplit 쿠키 파일에 직접 기록한다.
heartbeat나 유튜브 응답은 수정하지 않으므로 유튜브의 동시 재생 확인은 정상 동작한다.

XSplit은 이 쿠키의 만료일을 갱신하지 못하므로(갱신 응답도 `Partitioned`라서 버려짐),
발급 시점 기준 약 6개월 뒤 만료된다. 그래서 실행할 때마다 유효기간을 확인한다.

## 동작 흐름

```
1. %APPDATA%\XSplit\Broadcaster\cefcache\Network\Cookies 확인
   - 없으면(XSplit 최초 실행 전): XSplit만 실행하고 재실행 안내
2. 쿠키 파일 복사본에서 .youtube.com / VISITOR_INFO1_LIVE 만료 시각 조회
3. 30일 이상 남음 → XSplit 실행 후 종료
   30일 미만 / 만료 / 없음
     - XSplit 실행 중이면 → 종료 후 재실행 안내 (실행 중에는 파일이 잠겨 있고, 기록해도 재시작 전까지 반영되지 않음)
     - XSplit 꺼져 있으면 →
         a. 쿠키 없이 GET https://www.youtube.com/ → Set-Cookie에서 쿠키 4개 추출
         b. 쿠키 파일에 기록 (아래)
         c. 기록 결과 재확인
         d. XSplit 실행 후 종료
```

XSplit 경로: 저장된 경로 → 레지스트리 Uninstall `InstallLocation` → `%ProgramFiles%\XSplit\Broadcaster` → 파일 선택 창.

## 쿠키 파일 기록

| 항목 | 값 |
|---|---|
| 파일 | `%APPDATA%\XSplit\Broadcaster\cefcache\Network\Cookies` (SQLite, `meta.version = 18`) |
| 암호화 키 | `cefcache\LocalPrefs.json`의 `os_crypt.encrypted_key` → `"DPAPI"` 접두어 제거 → `CryptUnprotectData`(현재 사용자) |
| 값 형식 | `"v10"` + nonce(12) + AES-256-GCM 암호문 + 태그(16) |
| 행 속성 | `host_key=.youtube.com`, `top_frame_site_key=''`, `path=/`, `is_secure=1`, `samesite=0`(None), `source_scheme=2`, `source_port=443`, 만료일은 유튜브가 준 값 그대로 |

안전장치:

- 기록 전 기존 쿠키 하나를 복호화해 키가 맞는지 확인한다. (복호화한 값은 저장·전송하지 않는다)
- 스키마 버전이 18이 아니거나 암호화 형식이 `v10`이 아니면 기록하지 않고 XSplit만 실행한다.
- 하나의 트랜잭션으로 기록하고, 실패 시 롤백한다.
- XSplit 실행 중에는 기록하지 않는다.

외부 의존성 없이 Windows 기본 구성요소만 사용한다.

- SQLite: `winsqlite3.dll` (Windows 10 이상 내장)
- AES-GCM: `bcrypt.dll` (CNG) — .NET Framework 4.8에 `AesGcm`이 없어서 직접 호출
- DPAPI: `System.Security.Cryptography.ProtectedData`

## 하는 일 / 하지 않는 일

하는 일: 유튜브에서 visitor 쿠키 4개를 받아 XSplit 쿠키 파일에 저장하고, XSplit을 실행한다.

그 외에는 하지 않는다. 다른 쿠키·로그인 정보·XSplit 설정·방송 화면·영상 재생은 건드리지 않으며, 상주하지 않는다.
XSplit 개발자 모드(원격 디버깅 포트)도 필요 없다.

## 빌드

```bash
cd ChzzkDonationFix
dotnet build -c Release
```

출력: `ChzzkDonationFix/bin/Release/net48/ChzzkDonationFix.exe` — 이 파일 하나만 배포하면 된다.

요구 사항: Windows 10 이상, .NET Framework 4.8 (Windows 10 1903 이후 기본 포함)

## 사용

XSplit 바로가기 대신 `ChzzkDonationFix.exe`로 XSplit을 실행한다.

실행 기록: `%LOCALAPPDATA%\ChzzkDonationFix\log.txt`
저장된 XSplit 경로: `%LOCALAPPDATA%\ChzzkDonationFix\xsplit-path.txt`

## 유의점

- 서명되지 않은 실행 파일이라 SmartScreen 경고가 뜰 수 있다. 브라우저 쿠키 파일을 다루는 특성상 백신 오진 가능성도 있다.
- XSplit이 내장 브라우저를 Chrome 114 이상으로 올리면 이 문제 자체가 사라질 가능성이 높다. 그때 쿠키 파일 형식이 달라지면 이 프로그램은 기록하지 않고 XSplit만 실행한다.
- 유튜브가 기기 식별 방식을 바꾸면 효과가 없어질 수 있다.
- OBS에서도 드물게 같은 문구로 멈추는 경우가 보고되는데, 이 문서의 원인과는 다른 문제이며 이 프로그램으로 해결되지 않는다.

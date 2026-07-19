# AirLift

Windows 소리를 AirPlay(RAOP) 스피커로 보내는 **트레이 상주 프로그램**.

## 사용법

1. `AirLift.exe` 실행 → 작업 표시줄 트레이에 아이콘 생성
2. 트레이 아이콘 우클릭 → **스피커** → 목록에서 선택하면 연결·스트리밍 시작
3. 아이콘 더블클릭 = 마지막 스피커 연결/해제 토글

### 메뉴

| 항목 | 기능 |
|---|---|
| 스피커 | mDNS로 검색된 AirPlay 스피커 목록 (열 때마다 재검색) |
| 입력 소스 | 캡처할 출력 장치 선택 (기본: 기본 재생 장치, VB-CABLE 있으면 자동 선택) |
| 볼륨 +/- , 음소거 | AirPlay 수신기 볼륨 (0 ~ -30 dB) |
| 지연 모드 | 초저지연(~0.15초: 40ms+100ms) / 저지연(~0.25초: 60ms+150ms) / 안정(~0.45초: 150ms+250ms) |
| 자동 재연결 | 스트림 끊기면 2→5→10→15초 백오프로 재시도 (성공 시까지, 수동 해제 시 중단) |
| 시작 시 자동 연결 | 앱 시작 시 마지막 스피커로 자동 연결 |
| Windows 시작 시 실행 | 로그인 시 자동 실행 (HKCU Run 등록) |
| 가상 출력 장치 안내 | VB-CABLE 설치 안내 |
| 언어 / Language | 한국어 / English (전체 UI 즉시 전환, `Strings.cs`) |

## "출력 장치"로 쓰기 (VB-CABLE)

Windows에서 앱이 직접 가상 출력 장치를 만들려면 서명된 커널 오디오 드라이버가 필요하므로,
표준 방식인 무료 가상 케이블 드라이버를 사용한다:

1. [VB-CABLE](https://vb-audio.com/Cable/) 설치 (무료, 재부팅 필요할 수 있음)
2. Windows 소리 설정 → 출력 장치를 **CABLE Input** 으로 선택
3. AirLift [입력 소스] → **CABLE Input** (설치돼 있으면 자동 선택됨)
4. 스피커 연결 → 모든 소리가 로컬 스피커 대신 AirPlay로만 출력

VB-CABLE 없이 쓰면 기본 재생 장치를 미러링 (로컬 + AirPlay 동시 재생).

## 프로토콜 (RAOP / AirPlay v1 오디오)

1. **캡처** — WASAPI 루프백으로 선택한 재생 장치의 오디오 캡처
2. **변환** — Media Foundation 리샘플러로 44.1 kHz / 16-bit / 스테레오 PCM
3. **전송**
   - mDNS(`_raop._tcp`)로 스피커 검색, TXT 레코드로 암호화 방식 자동 판별
   - RTSP 핸드셰이크: `OPTIONS` → (`POST /auth-setup`) → `ANNOUNCE` → `SETUP` → `RECORD`
   - ALAC 무압축(escape) 프레이밍, 패킷당 352 프레임
   - RTP/UDP 오디오 + 컨트롤(sync/재전송) + 타이밍(NTP) 채널
   - 지원 기기에는 AES-128-CBC 암호화 (RSA-OAEP 키 교환)

## 기기 호환성

| TXT `et` 값 | 의미 | 지원 |
|---|---|---|
| `0` | 암호화 없음 | O |
| `1` | RSA/AES (AirPort Express, shairport-sync) | O |
| `4` | MFi/FairPlay — `auth-setup` 핸드셰이크 후 평문 전송 | O (자동 감지) |
| `3`, `5` | FairPlay v2 / HomeKit 페어링 (Apple TV, HomePod) | X |

- 비밀번호 걸린 스피커 미지원.
- 지연시간: 저지연 모드 약 0.25초, 안정 모드 약 0.45초. 재생 오프셋은 sync 패킷으로 송신 측이
  결정하므로 기기 보고값(11025 프레임)보다 낮춰 잡을 수 있음. 저지연이 특정 기기에서 끊기면 안정 모드 사용.

## 빌드

```
dotnet build                                          # 개발
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

산출물: `bin\Release\net9.0-windows\win-x64\publish\AirLift.exe` (.NET 9 Desktop Runtime 필요)

### 설치 프로그램 (MSI)

```
dotnet tool install --global wix --version 4.0.5      # 최초 1회 (WiX 7은 OSMF EULA 필요해서 4 사용)
powershell -File installer\build-installer.ps1
```

산출물: `installer\AirLift.msi` (자립형 — 대상 PC에 .NET 불필요). 설치 내용:
Program Files 설치 + 시작 메뉴 바로가기 + **인바운드 UDP 방화벽 허용 규칙**
(수신기가 보내는 NTP 타이밍/재전송 요청 수신용 — 없으면 재생 안 됨).

트레이 아이콘: 연결 시 화면 사각형 안에 실시간 레벨 미터(초록 막대) 표시 — 신호가 흐르는지 한눈에 확인.

## 구조

| 파일 | 역할 |
|---|---|
| `Program.cs` | 진입점, 단일 인스턴스 뮤텍스 |
| `TrayApp.cs` | 트레이 아이콘, 메뉴, 연결 오케스트레이션 |
| `StreamEngine.cs` | 세션 소유: RaopClient + AudioCapture + 페이싱 송신 스레드 |
| `RaopClient.cs` | RTSP 세션, RTP/컨트롤/타이밍 채널, AES/RSA, 재전송 버퍼 |
| `AudioCapture.cs` | WASAPI 루프백 캡처 + 리샘플링, 장치 열거 |
| `Discovery.cs` | mDNS 검색 + TXT 파싱 |
| `AlacEncoder.cs` | PCM → ALAC 무압축 프레임 |
| `NtpTime.cs` | NTP 타임스탬프 |
| `Settings.cs` | `%APPDATA%\AirLift\settings.json` |

## 구현 노트

- **ALAC 프레임 끝에 3비트 END 태그(값 7) 필수** — shairport-sync 5.x는 ffmpeg ALAC 디코더를
  쓰는데 END 태그 없으면 `no end tag found. incomplete packet` 으로 전 패킷 디코드 실패 → 무음.
  구형 RAOP 송신기들(raop_play, node_airtunes)은 안 붙였고 구형 hammerton 디코더는 관대했음.
  Apple 정식 인코더는 붙임 (스펙상 올바른 형식).
- 타이밍/컨트롤 리스너는 반드시 `SETUP` **전에** 시작 — 일부 기기(EDIFIER p20 펌웨어 등)는
  SETUP 처리 중 타이밍 포트를 프로브하고 응답을 기다림. 리스너 없으면 SETUP 응답이 영원히 안 옴.
- **송신 페이싱**: `timeBeginPeriod(1)`로 1ms 타이머 해상도 확보 (기본 15.6ms면 패킷 버스트 →
  수신기 지터). 150ms 선버퍼 + 버퍼 수위 기반 ±0.5% 레이트 서보로 송신 클럭을 캡처 클럭에 잠금.
  버퍼 부족 시 실제 오디오를 자르지 말고 통무음 패킷 전송. 리샘플은 WdlResampler(관리형) 사용 —
  MediaFoundation 리샘플러는 소스를 큰 덩어리로 불규칙하게 소비해서 버퍼 수위를 서보 신호로 못 씀.
  송신 스레드에서 동기 파일 I/O 금지 (디스크 플러시가 페이싱을 100ms+ 정지시킴).
- sync 패킷은 시작 시(확장 비트) + 약 1초마다 컨트롤 포트로 전송.
- 수신기 재전송 요청(0x55) 대응용 최근 512개 RTP 패킷 링버퍼 유지.
- 캡처 클럭과 송신 페이싱 간 드리프트는 버퍼 1초 초과 시 클리어로 보정.
- mDNS 검색은 한 번에 다 안 잡힐 수 있어 검색 결과를 누적 병합.

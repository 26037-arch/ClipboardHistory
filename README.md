# ClipboardHistory

Windows 10/11 로그인 사용자 세션에서 조용히 실행되며, Unicode 텍스트와 Windows 클립보드의 실제 이미지 데이터를 로컬에 기록하는 .NET 8 WinForms 백그라운드 앱입니다. `Ctrl+Alt+H`로 텍스트와 이미지 OCR 결과를 검색하고 원본을 다시 클립보드에 넣을 수 있습니다. 평상시 콘솔, 일반 창, 작업 표시줄 항목, 네트워크 통신, 관리자 권한 및 Windows 서비스를 사용하지 않습니다.

> 화면 캡처에는 개인정보, 인증 정보, 대화 내용 등이 포함될 수 있습니다. `Win+Shift+S` 캡처를 포함한 모든 클립보드 이미지가 이 PC의 사용자 전용 로컬 데이터 폴더에 저장될 수 있습니다.

## 저장 위치와 처리 구조

```text
숨겨진 NativeWindow (STA / WM_CLIPBOARDUPDATE)
  ├─ Unicode 텍스트 복제 → bounded queue(512) ─┐
  └─ 이미지 Bitmap 복제 → bounded queue(8)     ├─ 중앙 DatabaseWriter → SQLite WAL
                                                │
이미지 worker → PNG 인코딩/SHA-256/원자 저장 ───┤
OCR worker(동시 실행 1개) → Tesseract kor+eng ──┘
```

데이터 위치:

```text
%LOCALAPPDATA%\ClipboardHistory\clipboard.db
%LOCALAPPDATA%\ClipboardHistory\images\<SHA-256>.png
%LOCALAPPDATA%\ClipboardHistory\logs\clipboard-history.log
```

- 기존 텍스트 DB는 삭제하거나 초기화하지 않습니다. 시작할 때 기존 `clipboard_history` 테이블에 필요한 열과 인덱스를 반복 실행 가능한 migration으로 추가합니다.
- 텍스트, 이미지 행, OCR 결과, 삭제·복원, 영구 삭제 대상 확정, 설정 저장은 모두 하나의 bounded 중앙 writer 명령 채널에서 직렬화됩니다. 검색과 통계만 읽기 전용 연결을 사용합니다.
- 클립보드 listener에서는 OCR, PNG 압축, 파일 쓰기, SHA-256을 수행하지 않습니다. 텍스트와 이미지 형식이 한 이벤트에 함께 있으면 둘 다 저장합니다.
- 이미지 queue가 가득 차면 새 이미지를 즉시 버려 메모리 증가와 복사/붙여넣기 지연을 막습니다. 이미지나 OCR 원문은 로그에 기록하지 않습니다.
- PNG byte의 SHA-256을 파일명으로 사용합니다. 같은 이미지는 기존 파일을 재사용할 수 있으며 여러 기록이 같은 파일을 참조할 수 있습니다.
- 임시 파일을 먼저 쓰고 디스크 flush 후 같은 볼륨에서 최종 이름으로 이동합니다. DB 기록 실패 시 새로 만든 미참조 파일만 정리합니다.
- 상대 경로만 DB에 저장합니다. 읽기·삭제 전에 정규화된 전체 경로와 64자리 소문자 SHA-256 파일명을 검증하므로 이미지 루트 밖의 파일은 조작하지 않습니다.
- 시작 및 유지관리 시 임시 파일과 참조 없는 앱 관리 PNG를 조정합니다. 공유 PNG는 마지막 DB 참조가 영구 삭제될 때만 지웁니다.

## 이미지 및 메모리 안전 한도

- 이미지 queue: 8개
- 한 변 최대: 16,384 px
- 최대 픽셀: 40,000,000 px
- 추정 raw 메모리: 최대 160,000,000 bytes
- 인코딩된 PNG: 최대 100,000,000 bytes
- OCR queue: 128개, 실행 프로세스: 동시에 1개

한도를 넘거나 크기 정보가 비정상적인 이미지는 앱을 종료시키지 않고 거부합니다. 이 정책과 bounded queue 때문에 극단적인 이미지나 순간 폭주 중 일부 이벤트는 의도적으로 저장되지 않을 수 있습니다.

## Tesseract 한글·영어 OCR

OCR NuGet wrapper는 사용하지 않으며 설치된 `tesseract.exe`를 별도 프로세스로 실행합니다.

```text
C:\Program Files\Tesseract-OCR\tesseract.exe
C:\Program Files\Tesseract-OCR\tessdata\kor.traineddata
C:\Program Files\Tesseract-OCR\tessdata\eng.traineddata
```

실행 의미:

```text
tesseract <image.png> stdout --tessdata-dir <tessdata> -l kor+eng --oem 1 --psm 11
```

인자는 shell 문자열이 아니라 `ProcessStartInfo.ArgumentList`로 각각 전달합니다. stdout은 UTF-8로 읽고, 제한 시간은 30초이며 timeout·종료 시 전체 프로세스 트리를 종료합니다. stderr 원문은 로그에 남기지 않습니다.

앱 시작 시 EXE, tessdata 폴더, `kor.traineddata`, `eng.traineddata`, 실행 가능 여부와 `--list-langs`의 `kor`/`eng`를 확인합니다. OCR이 실패하거나 설치가 없어도 이미지와 텍스트 저장은 계속됩니다. 상태는 `pending`, `processing`, `completed`, `failed`, `unavailable` 중 하나이며, 비정상 종료 중 `processing`이던 행은 다음 시작에 `pending`으로 되돌려 재처리합니다.

self-contained EXE에는 Tesseract와 언어 모델이 포함되지 않습니다. EXE만 다른 PC로 옮기면 .NET 런타임 없이 앱 자체는 실행되지만, 동일 경로에 외부 Tesseract와 두 모델이 없으면 OCR은 동작하지 않습니다.

## 검색 팝업과 복원

`Ctrl+Alt+H`를 누르면 현재 커서가 있는 모니터에 작은 검색 팝업이 열립니다.

- 최근 활성 기록을 최대 100개 표시하며 휴지통 기록은 제외합니다.
- 150 ms debounce 후 텍스트 행은 `content`, 이미지 행은 `ocr_text`를 검색합니다.
- 기존과 동일하게 NFC, 대문자 통일, 연속 whitespace 축약을 적용합니다.
- 정확 일치 → 시작 일치 → 중간 포함 → 최신순으로 정렬합니다.
- 이미지 행에는 작은 썸네일, 복사 시각, 크기, OCR 미리보기와 상태가 표시됩니다.
- 검색창에서 `↓`, 목록에서 `↑`/`↓`, `Enter` 또는 마우스 클릭으로 선택합니다.
- 텍스트 선택 시 전체 Unicode 원문을, 이미지 선택 시 OCR 문자열이 아닌 저장된 PNG 원본을 클립보드에 넣습니다.
- 이미지 읽기·decode는 백그라운드에서 준비하고 최종 `Clipboard.SetImage`만 STA UI thread에서 실행합니다. 잠긴 클립보드는 20/50/100 ms 간격으로 재시도합니다.
- 텍스트 SHA-256 또는 이미지 SHA-256 자체 쓰기 토큰은 일치하는 다음 이벤트 한 번만 억제합니다. 다른 내용, 만료 토큰, 쓰기 실패는 정상 처리됩니다.
- 썸네일은 stream을 닫은 뒤 복제하며 현재 검색 결과에 대한 최대 24개만 보관합니다. 검색 교체·창 종료 시 dispose합니다.
- `Esc`, 팝업 바깥 클릭, 또는 단축키 재입력으로 팝업만 닫습니다.

오른쪽 아래 `모든 기록 보기`를 누르면 팝업이 닫히고 크기 조절 가능한 `전체 기록` 창이 열립니다. 이 창은 동시에 하나만 존재하고 항상 위에 고정되지 않으며, 닫아도 클립보드 감시는 계속됩니다.

## 전체 기록, 날짜 그룹, 저장 공간

전체 기록 창 상단에는 다음 범위를 비동기로 계산해 표시합니다.

- `clipboard.db`, `clipboard.db-wal`, `clipboard.db-shm`
- 실제 관리 이미지 PNG 전체
- 활성 기록이 참조하는 고유 이미지
- 활성 참조가 없고 휴지통 기록만 참조하는 고유 이미지
- 전체 DB 기록 수

Tesseract EXE와 tessdata는 앱 저장 공간에 포함하지 않습니다. 저장·삭제·복원 후 통계를 무효화해 다시 계산합니다.

활성 기록은 컴퓨터의 현재 지역 시간 기준 `연도 → 월 → 일 → 기록` 구조입니다. 최신 그룹부터 표시하고 각 단계에 기록 수와 이미지 용량을 표시합니다. 연·월·일 노드는 펼칠 때 읽기 전용 범위 query로 lazy load하며, 일별 기록은 한 번에 100개씩 `추가 로드`합니다. 날짜 query는 UTC 시작 이상·다음 경계 미만의 매개변수를 사용합니다.

개별 기록, 선택한 일·월·연도 그룹을 삭제할 수 있습니다. 확인창에는 기간, 기록 수, 이미지 수, 예상 이미지 용량과 `7일 동안 휴지통에서 복구할 수 있습니다.` 안내가 표시됩니다.

## 휴지통

일반 삭제와 자동 삭제는 `deleted_at_utc`만 기록하는 soft delete입니다. 이미지 파일은 즉시 지워지지 않으므로 삭제 직후 실제 디스크 사용량이 줄지 않을 수 있습니다.

- 삭제 시각, 원래 복사 시각, 영구 삭제 예정 시각, 남은 보관 일수를 표시합니다.
- 개별 복원과 체크한 여러 기록 복원을 지원합니다.
- 복원해도 원래 복사 시각과 날짜 그룹이 유지되고 일반 검색에 다시 나타납니다.
- 이미지 파일이 누락되거나 손상됐으면 기록은 유지하고 UI에 안전하게 표시합니다.
- 삭제 후 7일이 지난 행은 유지관리 작업에서 영구 삭제됩니다.
- `휴지통 전체 비우기`는 개수·용량을 다시 확인한 뒤 즉시 영구 삭제합니다.
- 영구 삭제 뒤 다른 활성/휴지통 행이 같은 경로 또는 해시를 참조하지 않을 때만 PNG를 삭제합니다. 파일 삭제 실패는 orphan 재시도 대상으로 남깁니다.

## 자동 삭제 설정

안전 기본값은 `자동 삭제 사용 = 꺼짐`, 선택 보관 기간은 `1년`입니다. 사용자가 직접 켜기 전에는 기존 기록을 자동 삭제하지 않습니다.

선택 가능 기간:

- 1개월: 현재 지역 시각에서 `AddMonths(-1)`
- 3개월: 현재 지역 시각에서 `AddMonths(-3)`
- 1년: 현재 지역 시각에서 `AddYears(-1)`

켜거나 켠 상태에서 기간을 변경하면 현재 대상 기록 수·이미지 수·예상 용량을 확인받습니다. 확인한 경우에만 설정을 중앙 writer로 저장하고 최초 정리를 실행합니다. `copied_at < cutoff UTC AND deleted_at_utc IS NULL`인 기록만 휴지통으로 이동하며 바로 영구 삭제하지 않습니다.

유지관리는 DB 초기화 뒤 한 번, 이후 최대 24시간에 한 번 실행됩니다. 설정 확인 직후에도 한 번 실행합니다. clipboard event마다 실행하지 않으며 gate로 두 작업의 동시 실행을 막고, 종료 시작 뒤 새 작업을 시작하지 않습니다.

## 빌드, 테스트, 게시

요구 환경은 Windows 10/11과 빌드용 .NET 8 SDK입니다. 앱 NuGet 의존성은 `Microsoft.Data.Sqlite` 8.0.20이며 OCR wrapper는 없습니다.

```powershell
dotnet restore .\ClipboardHistory.sln
dotnet build .\ClipboardHistory.sln -c Release --no-restore
dotnet test .\ClipboardHistory.sln -c Release --no-build
```

64비트 Windows용 self-contained 단일 EXE 게시:

```powershell
dotnet publish .\src\ClipboardHistory\ClipboardHistory.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\publish\win-x64
```

로그인 자동 실행 등록·해제:

```powershell
.\ClipboardHistory.exe --install-startup
.\ClipboardHistory.exe --remove-startup
```

동일 경로의 EXE를 교체하면 시작프로그램 바로가기를 다시 등록할 필요가 없습니다. EXE 위치가 바뀌면 새 위치에서 다시 등록합니다.

## 자동 테스트 범위

테스트는 기존 텍스트 동작 34개를 유지하면서 다음을 추가로 확인합니다.

- PNG round trip, SHA-256 파일명, 동일 이미지 재사용, traversal 차단, 새 orphan 정리
- 공유 이미지 파일의 중간 참조 유지와 마지막 참조 제거
- 기존 4열 DB의 무손실 반복 migration
- Tesseract `ArgumentList`, `kor+eng`, 설치 파일 검증, timeout·cancellation·프로세스 트리 종료
- OCR 성공·실패·unavailable, 중단 작업 복구, worker 동시 실행 1개
- 한글/영문 OCR 검색, whitespace 정규화, 휴지통 검색 제외
- 원본 이미지 decode/복원 경로와 이미지 자체 쓰기 1회 억제·다른 해시·만료
- 지역 시간 날짜 경계, 연·월·일 집계, 범위 soft delete와 복원
- 휴지통 7일 전 유지/경계 후 영구 삭제, 실제 저장 공간 분류
- 자동 삭제 기본 비활성, `AddMonths`/`AddYears`와 윤년, 경계 시각, 설정 영속화

실제 시스템 clipboard, Tesseract 설치, 포커스와 다중 모니터는 아래 수동 절차로 확인합니다.

## Windows 수동 테스트 절차

1. 앱을 실행하고 텍스트 한 줄·여러 줄·한글·영문·이모지를 복사합니다. 기존 연속 중복 방지와 `A, B, A` 저장을 확인합니다.
2. 그림판 이미지, 브라우저 이미지, `Win+Shift+S` 캡처를 복사하여 `%LOCALAPPDATA%\ClipboardHistory\images`에 PNG가 생기는지 확인합니다. 텍스트와 이미지를 함께 제공하는 형식도 두 기록이 남는지 확인합니다.
3. `Ctrl+Alt+H`에서 최근 텍스트/이미지, 썸네일, 이미지 크기, `텍스트 인식 중…`/`OCR 실패` 상태를 확인합니다.
4. 한글과 영문 OCR 일부를 대소문자와 공백을 바꿔 검색합니다. 이미지 항목을 클릭하거나 `Enter`로 선택한 뒤 그림판에 붙여넣어 OCR 글자가 아니라 원본 이미지가 복사됐는지 확인합니다. 이 자체 복원이 새 기록으로 한 번 더 저장되지 않는지도 확인합니다.
5. `모든 기록 보기`를 열고 창 크기 조절, 단일 창 유지, 연·월·일 lazy expand, 하루 100개 초과 시 추가 로드, 이미지 파일 누락 표시를 확인합니다.
6. 개별·일·월·연도 삭제 확인창의 기간·개수·이미지 수·용량·7일 안내를 확인합니다. 휴지통에서 개별/체크 복원 후 원래 날짜에 나타나는지 확인합니다.
7. 테스트용 기록으로 휴지통 전체 비우기의 2차 확인과 공유 PNG 마지막 참조 삭제를 확인합니다. 개인 기록을 테스트 자료로 사용하지 마십시오.
8. 자동 삭제 기본값이 꺼짐인지 확인합니다. 1개월·3개월·1년을 각각 켤 때 대상 요약 확인창이 나오고, 취소하면 설정과 기존 기록이 바뀌지 않는지 확인합니다.
9. Tesseract 언어 확인:

   ```powershell
   & "C:\Program Files\Tesseract-OCR\tesseract.exe" `
     --tessdata-dir "C:\Program Files\Tesseract-OCR\tessdata" `
     --list-langs
   ```

10. 개인정보가 없는 한글·영문 테스트 이미지를 별도로 만들어 OCR smoke test를 실행합니다. 사용자 클립보드 DB나 실제 캡처를 테스트 입력으로 사용하지 마십시오.
11. DPI와 배율이 다른 여러 모니터, 음수 좌표 배치에서 팝업 위치·포커스·썸네일을 확인합니다.
12. 빠른 이미지 폭주와 잠긴 clipboard 조건에서 다른 앱의 복사/붙여넣기가 멈추지 않고 앱이 오류창 없이 계속 실행되는지 확인합니다.
13. 정상 종료·로그아웃 후 queue flush, 다음 실행의 `pending` OCR 복구, DB/WAL 무결성을 확인합니다.

## 제한 사항

- OCR 품질은 외부 Tesseract와 설치된 `kor`/`eng` 모델, 캡처 해상도와 글꼴에 좌우됩니다.
- 검색은 정규화된 정확한 부분 문자열 검색이며 FTS5, 오타 교정, 유사 검색, 초성 검색, 정규식 검색은 제공하지 않습니다. DB가 커지면 검색 시간이 늘 수 있습니다.
- 파일 목록, HTML, RTF 및 사용자 지정 clipboard 형식 자체는 저장하지 않습니다. 그 이벤트에 Unicode 텍스트나 실제 이미지 데이터가 함께 있으면 해당 부분만 저장합니다.
- 로컬 DB와 PNG는 앱 자체 암호화를 적용하지 않습니다. 같은 Windows 사용자 권한을 얻은 프로그램은 파일을 읽을 수 있습니다.
- 강제 프로세스 종료는 정상 queue flush를 보장하지 않습니다. 저장 완료된 `pending`/`processing` OCR과 관리 파일은 다음 시작에 조정합니다.

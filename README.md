# WinCustoms

Windows 11 최적화 · 트윅 유틸리티. WinUI 3 (Windows App SDK) + .NET 9 + Native AOT 로 만든
단일 실행 파일 데스크톱 앱입니다. .NET 런타임 설치 없이 `WinCustoms.exe` 하나로 동작합니다.

---

## 무엇을 할 수 있나

| 카테고리 | 주요 항목 |
| --- | --- |
| 탐색기 · 우클릭 | Windows 10 클래식 우클릭 메뉴, 홈/갤러리 숨김, '내 PC'로 열기, 확장명·숨김 파일 표시, Compact 보기 |
| 우클릭 프로그램 등록 및 제거 | 메뉴에 올라온 항목을 훑어 토글로 숨김·복원, 원하는 `.exe` 를 파일/폴더/폴더 배경 메뉴에 추가·삭제 |
| 작업 표시줄 · 시작 | 왼쪽 정렬, 단추 결합 안 함, 웹 검색 패널 끄기, 검색용 브라우저 선택, 아이콘·배지 정리, 추천/점프목록 끄기 |
| 개인정보 · 광고 | 텔레메트리·광고 ID, 팁/Spotlight, 활동 기록·위치·클립보드, 입력 개인화, Copilot/Recall, Edge 상주 차단 |
| 기본 앱 정리 | Xbox · Solitaire · 뉴스 · Teams 등 24종 화이트리스트 기반 선택 제거 |
| 프로그램 설치 | Win11 설치 직후용 추천(ExplorerPatcher·Open-Shell·런타임 등) + winget 검색 설치 |
| 시스템 백업 | C: .wim 백업 · WinRE 자동 복원(명령 입력 없이 다시 시작 후 적용) |
| 커스텀 Win11 ISO | 순정 ISO에 트윅·디블로트 이식, 설치 요구사항 우회·현재 PC 드라이버 주입 옵션 (ADK oscdimg 필요) |
| 성능 최적화 | Ultimate Performance, 애니메이션 끄기, 다운로드 최적화·Game DVR·Prefetch 끄기, NTFS/네트워크 스로틀링 조정, 종료 대기 단축 |
| 파워유저 도구 | 소유권 가져오기, 여기서 터미널 열기, 임시 파일 정리, 시스템 복원 지점 생성 |

---

## 안전 설계

이 앱의 설계에서 가장 신경 쓴 부분은 "언제든 되돌릴 수 있는가"입니다.

1. **적용/복원이 구조적으로 쌍을 이룹니다.**
   레지스트리 트윅은 `RegistryValueSpec` 하나에 *적용 값*과 *기본값*을 함께 적습니다.
   `TweakFactory` 가 이 스펙에서 적용·복원·상태 감지를 모두 생성하므로
   복원 로직을 빠뜨린 트윅은 애초에 만들 수 없습니다.

2. **변경 전 자동 백업.**
   트윅을 적용하기 직전 관련 키를 `.reg` 파일로 내보냅니다.
   저장 위치는 `%LOCALAPPDATA%\WinCustoms\Backups` 이며, 앱 없이 더블클릭만으로 되돌릴 수 있습니다.

3. **토글은 예약, 적용은 한 번에.**
   토글을 움직여도 즉시 반영되지 않고 "선택 항목 적용"을 눌러야 실행됩니다.
   실수로 건드린 항목을 되돌릴 여유가 생기고, UAC 창도 한 번만 뜹니다.

4. **최소 권한 실행.**
   앱 자체는 표준 권한(`asInvoker`)으로 뜹니다.
   HKLM 을 건드려야 할 때만 작업 목록을 JSON 으로 만들어 자기 자신을 `runas` 로 짧게 재실행합니다
   (`ElevationService` → `ElevatedJobHost`). 승격 인스턴스는 XAML 을 초기화하지 않고 수십 ms 안에 끝납니다.

5. **위험 등급 표시.**
   되돌리기가 완전하지 않은 항목(`TweakRisk.High`)은 적용 전에 별도 확인을 받습니다.

---

## 구조

```
src/WinCustoms/
├── Program.cs                    직접 작성한 진입점 (승격 작업이면 XAML 없이 처리 후 종료)
├── App.xaml(.cs)                 DI 컨테이너 구성, 전역 스타일
├── MainWindow.xaml(.cs)          Mica 배경 + NavigationView + 커스텀 타이틀바
├── ServiceConfiguration.cs       서비스/뷰모델 등록
│
├── Common/
│   ├── RegistryPrimitives.cs     RegistryRoot · RegistryOperation · RegistryValueSpec · 값 코덱
│   ├── RegistryPaths.cs          레지스트리 경로 상수 모음
│   ├── ElevatedJob.cs            승격 작업 모델 + JSON 소스 생성 컨텍스트
│   ├── ElevatedJobHost.cs        승격 프로세스 쪽 실행기
│   └── NativeMethods.cs          LibraryImport 기반 P/Invoke
│
├── Models/
│   ├── TweakItem.cs              트윅 한 개 (상태 · 적용/복원 액션 · 메타데이터)
│   └── TweakFactory.cs           스펙 → 트윅 생성기
│
├── Services/
│   ├── RegistryService.cs        읽기/쓰기/삭제 + .reg 백업, 하이브별 승격 분기
│   ├── ShellService.cs           explorer.exe 재시작, 셸 통지, 프로세스/PowerShell 실행
│   ├── ElevationService.cs       UAC 승격 위임
│   ├── MaintenanceService.cs     전원 구성표 · 임시 파일 · 복원 지점
│   ├── AppxService.cs            기본 앱 목록/제거
│   ├── WingetService.cs          winget 패키지 카탈로그 · 설치
│   ├── SystemImageService.cs     시스템 WIM 캡처 · 복원 (DISM/VSS)
│   ├── ContextMenuService.cs     사용자 우클릭 항목 등록/제거
│   ├── ShellMenuInventoryService.cs  시스템 전체 우클릭 항목 수집 · 숨김/복원
│   ├── DialogService.cs          ContentDialog · 파일 피커
│   ├── TweakEngine.cs            배치 실행 · 오류 수집 · 재시작 필요 판단
│   └── Catalog/                  카테고리별 트윅 정의 (partial 분할)
│
├── ViewModels/                   MainViewModel + 카테고리별 뷰모델
└── Views/                        TweakListPage(공용) · ContextMenuEditorPage · DebloatPage · WingetPage · SystemBackupPage · SettingsPage
```

`ContextMenuEditorPage` 는 SelectorBar 로 두 탭을 나눈다. 기본은 **제거** 탭이고, **등록** 탭이 두 번째다.

---

## 빌드

### 사전 준비

이 저장소를 빌드하려면 **.NET 9 SDK 이상**이 필요합니다 (런타임만으로는 부족합니다).

```powershell
winget install Microsoft.DotNet.SDK.9
```

Native AOT 게시에는 **MSVC 링커와 Windows SDK** 가 추가로 필요합니다.
이미 Visual Studio 나 Build Tools 에 C++ 워크로드가 있다면 그대로 쓰면 됩니다.
설치 여부는 아래로 확인할 수 있습니다.

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -products * -all -format value -property installationPath
# 각 경로 아래 VC\Tools\MSVC 폴더가 있으면 준비된 것입니다.
```

없다면 C++ 빌드 도구를 설치합니다.

```powershell
winget install Microsoft.VisualStudio.2022.BuildTools `
  --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621"
```

> Build Tools 가 이미 설치돼 있는데 C++ 워크로드만 없다면, 위 명령은 "이미 설치됨"으로
> 종료 코드 1 을 내고 아무것도 하지 않습니다. 그럴 때는 `install` 대신 `modify` 를 쓰세요.
>
> ```powershell
> & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify `
>   --installPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" `
>   --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --quiet --norestart --wait
> ```

### 개발 빌드 (AOT 없음, 빠름)

```powershell
dotnet build src/WinCustoms/WinCustoms.csproj -c Debug -r win-x64
```

실행 파일은 `src\WinCustoms\bin\x64\Debug\net9.0-windows10.0.22621.0\win-x64\WinCustoms.exe` 입니다.
(`Platform` 이 `x64` 로 고정돼 있어 출력 경로에 `x64` 가 한 단계 더 들어갑니다.)

### Native AOT 단일 실행 파일 게시

```powershell
dotnet publish src/WinCustoms/WinCustoms.csproj -c Release -r win-x64 -o publish
```

결과물은 `publish\WinCustoms.exe` (약 7.4 MB) 이고, 배포 폴더 전체는 약 60 MB 입니다.
`WindowsAppSDKSelfContained` + `SelfContained` 조합이라 Windows App SDK 런타임도 함께 포함되며,
대상 PC 에 .NET 이나 WinAppSDK 를 별도로 설치할 필요가 없습니다.
폴더째로 복사해서 쓰면 됩니다.

ARM64 기기용은 `-r win-arm64` 로 바꿔서 게시하세요.

---

## 릴리스 배포

코드를 고쳤다면 이 한 줄이면 끝입니다. 커밋도 푸시도 스크립트가 합니다.

```powershell
.\scripts\release.ps1 -Message "무엇을 고쳤는지"
```

이미 커밋해 둔 상태라면 `-Message` 없이 그냥 돌리면 됩니다.
반대로 `-Message` 를 주지 않았는데 커밋되지 않은 변경이 남아 있으면 빌드 전에 멈춥니다.

버전 번호를 직접 만질 일은 없습니다. 스크립트가 알아서 올리고 `csproj` 수정까지 커밋해 줍니다.

### 버전이 정해지는 규칙

| 상황 | 결과 |
| --- | --- |
| `csproj` 의 버전이 아직 배포되지 않음 | 그 버전을 그대로 사용 (첫 릴리스가 여기 해당) |
| 이미 배포된 버전 | 끝자리를 1 올림 (`1.0.0` → `1.0.1`) |
| `-Minor` | 가운데 자리를 올림 (`1.0.3` → `1.1.0`). 기능을 추가했을 때 |
| `-Major` | 앞자리를 올림 (`1.4.2` → `2.0.0`). 크게 갈아엎었을 때 |
| `-Version 2.0.0` | 지정한 값 그대로 |

다음에 몇 번이 나올지 미리 보려면 `-ShowVersion` 을 붙이세요. 아무것도 바꾸지 않고 알려만 줍니다.

```powershell
.\scripts\release.ps1 -ShowVersion          # 다음 릴리스는 v1.0.1
.\scripts\release.ps1 -SkipUpload           # 빌드와 zip 만. 버전도 안 건드림
.\scripts\release.ps1 -Draft                # 초안으로 올리고 웹에서 직접 공개
```

### 스크립트가 하는 일

1. `-Message` 가 있으면 남은 변경을 전부 커밋합니다. 그다음 원격이 앞서 있지 않은지 확인하고,
   태그를 받아 어떤 버전이 이미 나갔는지 확인합니다.
2. 위 규칙으로 버전을 정하고 `csproj` 의 `<Version>` 을 고칩니다.
3. Native AOT 로 게시합니다.
4. **필수 파일을 검증합니다.** XBF 와 `WinCustoms.pri` 는 게시 목록에서 조용히 빠지는 일이 있는데,
   빠진 채로 배포하면 사용자 PC 에서 실행 즉시 `XamlParseException` 으로 죽습니다.
   빌드는 성공하고 배포본만 망가지는 유형이라 자동 검증이 필요합니다.
5. 게시된 exe 를 실제로 띄워 창이 뜨는지 확인합니다 (`-SkipSmokeTest` 로 건너뜀).
6. 압축을 풀면 `WinCustoms` 폴더가 나오도록 zip 으로 묶습니다 (약 23 MB).
7. **여기까지 통과한 뒤에야** 버전 변경을 커밋하고, 밀린 커밋까지 함께 푸시합니다.
   빌드나 검증에서 실패하면 `csproj` 를 원래대로 되돌리고 멈추므로, 나가지도 못한 버전 번호가
   커밋 기록에 남지 않습니다.
8. SHA256 과 설치 안내를 담은 릴리스 노트를 붙여 업로드합니다.
   태그는 `--target` 으로 방금 빌드한 커밋에 고정되므로, 배포한 바이너리와 태그가 가리키는
   소스가 항상 일치합니다.

태그는 GitHub 쪽에 생기고 스크립트가 마지막에 `fetch` 로 로컬에 가져옵니다.
`dist\` 는 `.gitignore` 에 있어 커밋되지 않습니다.

### 같은 버전으로 zip 만 갈아끼우기

버전 번호를 올리지 않고, 이미 올라간 최신 릴리스의 실행 파일(zip)만 새 빌드로 바꿀 때:

```powershell
.\scripts\replace-release.ps1 -Message "핫픽스 설명"
.\scripts\replace-release.ps1 -Tag v1.0.0   # 특정 태그 지정
.\scripts\replace-release.ps1 -SkipUpload   # 로컬 zip 만
```

기존 릴리스 자산을 삭제한 뒤 새 zip 을 올리고, 노트·SHA256 을 갱신합니다. `csproj` 의 `<Version>` 은 건드리지 않습니다.

---

## 동작 원리 메모

### 클래식 우클릭 메뉴는 왜 레지스트리 키 하나로 바뀌나

Windows 11 의 새 컨텍스트 메뉴는 CLSID `{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}` 셸 확장이 그립니다.
`HKCU\Software\Classes\CLSID\{86ca1aa0-…}\InprocServer32` 의 **기본값을 빈 문자열로** 만들어 두면
셸이 이 확장의 DLL 경로를 찾지 못해 로드에 실패하고, 예전 메뉴로 되돌아갑니다.

키만 만들고 기본값을 설정하지 않으면 동작하지 않는다는 점이 흔한 함정입니다.
복원은 CLSID 키를 통째로 삭제하면 되므로 시스템 파일이나 HKLM 은 전혀 건드리지 않습니다.

### 우클릭 항목을 지우지 않고 숨기는 방법

'제거' 탭은 키를 삭제하지 않는다. 프로그램을 재설치하면 되살아나는 데다, 잘못 지웠을 때
되돌릴 방법이 사라지기 때문이다. 대신 등록 방식에 따라 두 가지 수단을 쓴다.

| 등록 방식 | 위치 | 숨기는 법 | 되돌리는 법 |
| --- | --- | --- | --- |
| 동사(verb) | `<클래스>\shell\<동사>` | `LegacyDisable` 값을 빈 문자열로 추가 | 값 삭제 |
| 셸 확장(shellex) | `<클래스>\shellex\ContextMenuHandlers\…` | 차단 목록에 CLSID 추가 | 값 삭제 |

차단 목록은 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` 하나뿐이라
셸 확장을 끄고 켤 때는 반드시 UAC 가 뜬다. 이미 탐색기에 로드된 DLL 은 즉시 내려가지 않으므로
'탐색기 다시 시작'을 한 번 눌러야 반영된다. 동사 쪽은 UAC 없이 바로 반영된다(HKCU 항목인 경우).

같은 프로그램이 파일·폴더·폴더 배경·드라이브에 따로 등록하는 일이 흔해서, 동사는 이름으로 묶어
한 항목으로 보여주고 토글 한 번에 모든 범위를 함께 처리한다.

실행 파일이 Windows 폴더 안에 있으면 OS 기본 항목으로 보고 기본 목록에서 감춘다.
`powershell.exe` 처럼 경로 없이 등록된 명령은 System32 와 PATH 를 뒤져 실제 파일을 찾은 뒤 판단한다.

### 왜 앱 전체를 관리자로 띄우지 않나

관리자 프로세스에서 `HKEY_CURRENT_USER` 에 쓰면 승격에 사용된 계정의 하이브에 기록될 수 있고,
UIPI 때문에 탐색기에서 창으로 파일을 끌어다 놓는 것도 막힙니다.
그래서 UI 는 표준 권한으로 유지하고, HKLM 작업만 별도 프로세스로 짧게 승격시킵니다.

---

## csproj 에서 건드리면 앱이 죽는 설정들

전부 실제로 한 번씩 밟은 것들이라, 값을 바꾸기 전에 읽어 두세요.

| 설정 | 왜 이 값이어야 하나 |
| --- | --- |
| `LangVersion` = `preview` | WinUI 3 + AOT 에서는 `[ObservableProperty]` 를 partial 프로퍼티에 붙여야 CsWinRT 가 WinRT 마샬링 코드를 만든다. 그 구현부가 `field` 키워드를 쓰므로 preview 가 필요하다. 필드 방식으로 되돌리면 MVVMTK0045 경고와 함께 `x:Bind` 가 깨진다. |
| `WindowsAppSdkBootstrapInitialize` = `false` | self-contained 인데 부트스트래퍼를 켜면 "설치된" WindowsAppRuntime 프레임워크 패키지와 앱 폴더의 런타임이 같이 로드되어 `CoreMessagingXP.dll` 에서 fail-fast (0xC0000602) 한다. |
| `Microsoft.WindowsAppSDK.WinUI` 참조 | 메타패키지(`Microsoft.WindowsAppSDK`)를 쓰면 Windows ML 이 딸려 와 `onnxruntime.dll`(21MB) + `DirectML.dll`(18MB) 이 배포본에 그대로 들어간다. |
| `IncludeXamlAssetsInPublish` 타깃 | XAML 컴파일러는 XBF 와 `WinCustoms.pri` 를 `OutputPath` 로 복사만 하고 게시 목록에는 넣지 않는다. 이 타깃이 없으면 게시본 실행 즉시 `XamlParseException` (0x802B000A) 으로 죽는다. |
| `RemoveNativePdbFromPublish` 타깃 | `_CopyAotSymbols` 가 `Publish` **이후에** PDB(약 39MB)를 다시 복사하므로 반드시 그 타깃 뒤에 걸어야 한다. `AfterTargets` 에 `Publish` 를 같이 적으면 먼저 소진되어 효과가 없다. |

시작 단계에서 문제가 생기면 `%LOCALAPPDATA%\WinCustoms\startup.log` 를 먼저 확인하세요.
창이 뜰 때까지의 예외만 기록하고, 그 뒤에는 성능을 위해 기록을 끕니다.

---

## 알려진 제약

- **Prefetch · Superfetch 끄기**는 SSD·메모리가 충분한 PC 에 맞춘 옵션입니다. HDD 나 저메모리 환경에서는 앱 실행이 느려질 수 있습니다.
- **시작 메뉴 '추천' 영역 완전 제거**는 Pro/Enterprise 정책(`HideRecommendedSection`)이 필요합니다.
  Home 에디션에서는 목록만 비워집니다.
- **기본 앱 제거**는 현재 사용자 계정 기준입니다. 복구는 Microsoft Store 에서 수동 재설치로만 가능합니다.
- **Windows 7 스타일 시작 메뉴**는 Windows 자체 설정으로 구현할 수 없어 Open-Shell 설치 안내만 제공합니다.

---

## 라이선스

[MIT License](LICENSE). 자유롭게 사용·수정·재배포할 수 있으며, 저작권 표시와 라이선스 전문만 함께 남겨 주세요.

### 사용 전 주의

이 프로그램은 시스템 레지스트리를 변경합니다. 중요한 작업 중이거나 백업이 없는 상태에서는 사용하지 마세요.
트윅을 적용하기 전에 [복원 지점] 버튼으로 시스템 복원 지점을 만들어 두는 것을 권합니다.
MIT 라이선스에 명시된 대로 이 소프트웨어는 어떠한 보증도 제공하지 않으며, 사용에 따른 책임은 사용자에게 있습니다.

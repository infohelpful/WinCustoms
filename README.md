# WinCustoms

Windows 11 최적화 · 트윅 유틸리티. WinUI 3 (Windows App SDK) + .NET 9 + Native AOT 로 만든
단일 실행 파일 데스크톱 앱입니다. .NET 런타임 설치 없이 `WinCustoms.exe` 하나로 동작합니다.

---

## 무엇을 할 수 있나

| 카테고리 | 주요 항목 |
| --- | --- |
| 탐색기 · 우클릭 | Windows 10 클래식 우클릭 메뉴, 홈/갤러리 숨김, 리본 UI, '내 PC'로 열기, 확장명·숨김 파일 표시, Compact 보기 |
| 우클릭 프로그램 등록 | 원하는 `.exe` 를 파일/폴더/폴더 배경 우클릭 메뉴에 추가·삭제 |
| 작업 표시줄 · 시작 | 왼쪽 정렬, 위젯/검색/작업 보기/Copilot 아이콘 숨김, 시계 초 표시, Bing 웹 검색 차단, '추천' 영역 정리, Open-Shell 안내 |
| 개인정보 · 광고 | 텔레메트리·광고 ID 차단, Copilot/Recall 비활성화, Edge 백그라운드 상주 차단 |
| 기본 앱 정리 | Xbox · Solitaire · 뉴스 · Teams 등 24종 화이트리스트 기반 선택 제거 |
| 성능 최적화 | Ultimate Performance 전원 구성표, 애니메이션·투명도 끄기, 드라이버 자동 업데이트 차단, 종료 대기 시간 단축 |
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
│   ├── ContextMenuService.cs     사용자 우클릭 항목 등록/제거
│   ├── DialogService.cs          ContentDialog · 파일 피커
│   ├── TweakEngine.cs            배치 실행 · 오류 수집 · 재시작 필요 판단
│   └── Catalog/                  카테고리별 트윅 정의 (partial 분할)
│
├── ViewModels/                   MainViewModel + 카테고리별 뷰모델
└── Views/                        TweakListPage(공용) · ContextMenuEditorPage · DebloatPage · SettingsPage
```

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

## 동작 원리 메모

### 클래식 우클릭 메뉴는 왜 레지스트리 키 하나로 바뀌나

Windows 11 의 새 컨텍스트 메뉴는 CLSID `{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}` 셸 확장이 그립니다.
`HKCU\Software\Classes\CLSID\{86ca1aa0-…}\InprocServer32` 의 **기본값을 빈 문자열로** 만들어 두면
셸이 이 확장의 DLL 경로를 찾지 못해 로드에 실패하고, 예전 메뉴로 되돌아갑니다.

키만 만들고 기본값을 설정하지 않으면 동작하지 않는다는 점이 흔한 함정입니다.
복원은 CLSID 키를 통째로 삭제하면 되므로 시스템 파일이나 HKLM 은 전혀 건드리지 않습니다.

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

- **리본 탐색기**(`explorer.ribbon-ui`)는 Windows 11 21H2 · 22H2 에서만 동작합니다.
  23H2 이후 빌드에는 해당 셸 확장이 남아 있지 않아 토글해도 변화가 없습니다.
- **시작 메뉴 '추천' 영역 완전 제거**는 Pro/Enterprise 정책(`HideRecommendedSection`)이 필요합니다.
  Home 에디션에서는 목록만 비워집니다.
- **기본 앱 제거**는 현재 사용자 계정 기준입니다. 복구는 Microsoft Store 에서 수동 재설치로만 가능합니다.
- **Windows 7 스타일 시작 메뉴**는 Windows 자체 설정으로 구현할 수 없어 Open-Shell 설치 안내만 제공합니다.

---

## 라이선스

이 프로젝트는 시스템 설정을 변경합니다. 중요한 작업 중이거나 백업이 없는 상태에서는 사용하지 마세요.
사용에 따른 책임은 사용자에게 있습니다.

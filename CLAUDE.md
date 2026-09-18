# CLAUDE.md

프로젝트 전용 규칙. Claude Code(및 사람)는 아래 규칙을 반드시 지킬 것.

## 이미 완성되어 정상 동작하는 기능은 건드리지 말 것

어떤 기능이 한 번 완성되어 정상 동작하는 게 확인됐으면(실사용/재설치
테스트로 검증됐거나, 사용자가 "됐다"고 확인한 상태), **그 기능을
사용자가 명시적으로 "이거 고쳐줘/수정해줘"라고 지목하지 않는 이상
절대 손대지 않는다.** 다른 기능을 고치거나 새 기능을 추가하는 김에
"이 참에 정리/개선"하는 식으로 완성된 기능의 코드를 리팩터링·단순화·
스타일 변경하는 것도 금지.

**이유:** 이 프로젝트는 부팅 USB/커스텀 ISO처럼 겉보기엔 사소해
보이는 조건 하나(레지스트리 키 하나, 파티션 타입 하나, unattend.xml
태그 하나)가 실제 설치 성공/실패를 가르는 코드가 많다. "정리"가
목적이었던 수정이 실제로는 회귀였던 사례가 여러 번 반복됐다(아래
부팅 USB 섹션 참고). 다른 작업을 하다가 "이 코드 이상해 보이는데
고쳐야겠다"는 충동이 들어도, 그게 지금 하는 작업과 무관한 이미 동작
중인 기능이면 그냥 둔다.

**적용 범위:** 버그 수정 요청이 들어온 그 기능/파일에 한정해서만
수정한다. 같은 파일 안에 있어도 요청받지 않은 다른 메서드/기능은
건드리지 않는다. 오류가 실제로 발생해서 사용자가 고쳐달라고 한
경우에만 예외.

## 부팅 USB 파티션/응답파일 로직 — 절대 임의로 "정리/단순화"하지 말 것

대상: `src/WinCustoms/Common/BootUsbJobHost.cs` → `PrepareTargetVolume()`,
`src/WinCustoms/Common/CustomIsoJobHost.cs` → `PatchBootWim()`

과거 여러 번(`335f95d` → `4c63f6d` → `43397d4`) 이 부분을 "단순화"하다가
**USB가 UEFI로 부팅이 안 되거나, 무인 설치 응답파일을 Setup이 못 찾는**
회귀가 실제로 반복 발생했다. 자세한 경위는
`docs/boot-usb-partitioning-incident.md` 참고.

### 반드시 지킬 것

1. **`autounattend.xml`은 파티션 루트에 파일로 두는 것만으로는 부족하다 —
   반드시 `boot.wim` 안에도 `Autounattend.xml`로 심는다**
   (`CustomIsoJobHost.PatchBootWim`, `autounattendPath` 매개변수).
   이유: USB에 파티션이 2개 이상이면 Windows가 그 디스크를 "이동식"이
   아니라 "고정 디스크"로 인식해버리는데(`Get-Volume`의 `DriveType`이
   `Fixed`로 바뀜, 실측 확인됨), Windows Setup의 autounattend.xml
   자동탐색은 이동식 미디어만 검사한다. `boot.wim` 안에 심으면 WinPE가
   그 WIM 자체에서 부팅하므로 파티션 구성/이동식 여부와 무관하게 항상
   찾는다 — Rufus(`wue.c`)가 쓰는 것과 같은 방식이다. 이 주입 로직을
   지우고 파티션 루트 파일 복사만 남기지 말 것.
2. **GPT+NTFS는 `dual = true`(EFI 1GB FAT32 + 데이터 NTFS 2개 분리)를
   유지한다.** 위 1번이 적용된 상태에서는 파티션이 2개여도 응답파일
   탐색 문제가 없으므로 `dual = false`로 억지로 통일할 필요 없다.
3. **GPT + 단일 파티션(dual=false, 즉 GPT+FAT32 또는 MBR) 경로는 반드시
   `create partition efi`로 만든다.** `create partition primary`로
   만들면 파티션은 생기지만 ESP로 마킹되지 않아 UEFI 펌웨어가 부팅
   가능한 파티션으로 인식하지 못한다.
3-1. **반대로 `dual=true`(GPT+NTFS)일 때 EFI/부팅용 작은 파티션은
   `create partition efi`로 만들면 안 되고 `create partition primary`로
   만들어야 한다** (2번과 모순처럼 보이지만 다른 얘기 — 이건 "USB 자체가
   2개짜리 파티션일 때"의 얘기). 진짜 ESP GPT 타입으로 마킹하면, 실제
   컴퓨터에 설치할 때 Windows Setup이 "시스템에 이미 ESP가 있다"고
   착각해서(우리 USB 자체의 ESP를 재사용하려 함) **설치 대상 디스크에
   ESP를 새로 안 만들고 MSR+주 파티션만 만들어버리는 회귀**가 실측
   확인됐다(2026-09-17). Rufus(`drive.c`, `UEFI:NTFS` 파티션)도 정확히
   이 이유로 보조 부팅 파티션을 `PARTITION_MICROSOFT_DATA`(일반 데이터
   타입)로 만든다 — 펌웨어는 이동식 매체 부팅 시 GPT 타입을 안 따지고
   FAT 파티션에서 `\EFI\Boot\bootx64.efi`를 찾으므로 ESP로 안 마킹해도
   부팅엔 지장 없다. `docs/boot-usb-partitioning-incident.md` 후속 수정
   4 참고.
4. **파티션 생성에 PowerShell `New-Partition`을 쓰지 않는다** —
   `diskpart`의 `create partition primary` / `create partition efi`를
   쓴다. 단, **diskpart로 만들어도 GPT로 바뀌는 순간 Windows가
   MSR(예약) 파티션을 자기 멋대로 끼워넣는 건 막을 방법이 없다**
   (실측: convert+create를 한 diskpart 세션에 합쳐도 똑같이 생김).
   막으려 하지 말고, 원하는 파티션을 만든 직후 `Get-Partition`으로
   `Type -eq 'Reserved'`인 파티션을 찾아 무조건 지운다.
5. 이 로직을 고칠 일이 생기면, 코드를 고치기 전에
   `docs/boot-usb-partitioning-incident.md`를 먼저 읽고 왜 이런 제약이
   있는지 이해한 뒤에 수정한다.

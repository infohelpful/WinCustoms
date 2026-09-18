# Ventoy 번들 안내

이 폴더의 `Ventoy2Disk.exe`와 `boot/`, `ventoy/`, `plugin/` 디렉터리는
[Ventoy](https://www.ventoy.net) 프로젝트(저작권자: longpanda, admin@ventoy.net)의
**공식 배포 바이너리(v1.1.17)를 그대로 동봉**한 것입니다. WinCustoms가 이 소스를
재컴파일하거나 수정하지 않았습니다 — diskpart.exe/dism.exe/oscdimg.exe와 동일하게,
외부 프로그램을 그대로 실행하는 방식으로 통합합니다.

- 프로젝트: https://github.com/ventoy/Ventoy
- 라이선스: GPLv3+ (`gpl-3.0.txt` 동봉)
- 배포 출처: https://github.com/ventoy/Ventoy/releases/tag/v1.1.17
  (`ventoy-1.1.17-windows.zip`)

WinCustoms는 Ventoy2Disk.exe를 `VTOYCLI` 명령줄 모드로 호출해서 부팅 USB에
Ventoy를 설치/업데이트합니다 (`docs/ventoy-style-feature-plan.md`,
`docs/ventoy-feature-devplan.md` 참고).

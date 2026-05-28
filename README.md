# MyGitPuller

여러 Git 리포지토리를 병렬로 빠르게 업데이트하는 C# 애플리케이션입니다. CLI는 계속 지원되며, WinUI 3 GUI 프로젝트도 함께 제공합니다. 상위 디렉터리를 스캔하여 모든 리포지토리를 찾고, 원격 상태를 로컬 백업용으로 강제 동기화합니다.

## 시작하기

### 필수 조건

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 이상 (실행 시)
- 또는 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (직접 빌드 시)
- Git이 설치되어 있고 시스템 PATH에 등록되어 있어야 합니다.
- Git LFS 오브젝트까지 백업하려면 Git LFS가 설치되어 있고 시스템 PATH에 등록되어 있어야 합니다. 설치되어 있지 않으면 LFS 단계는 자동으로 생략됩니다.
- GUI를 빌드하거나 실행하려면 Windows 10 1809 이상, .NET 8 SDK, Windows App SDK/WinUI 3 개발 환경이 필요합니다.

### 설치 및 실행

1. **빌드된 파일 사용**: `Publish` 폴더의 `GitPuller.exe`와 `pull.bat`를 원하는 위치로 복사합니다.
2. **실행**: `pull.bat`를 더블 클릭하거나 터미널에서 실행합니다.

```bash
pull.bat
```

### WinUI GUI

GUI는 `GitPuller.WinUI` 프로젝트에 있습니다. CLI와 같은 Core 실행 로직을 사용하므로 동기화 의미는 동일하지만, 저장소 목록 관리와 결과 확인을 GUI에서 수행할 수 있습니다.

```powershell
dotnet run --project GitPuller.WinUI\GitPuller.WinUI.csproj --configuration Debug
```

GUI의 기본 화면은 다크 테마이며 다음 흐름을 제공합니다.

- 라이브러리 루트와 카테고리별 저장소 목록 확인
- 전체 동기화 실행, 진행률 확인, 실패/경고/업데이트/정상 순 정렬
- 정상 저장소는 기본적으로 숨김 처리
- 결과 선택 시 진단 제목, 설명, 권장 조치, 재시도 정책, 관련 로그 확인
- URL에서 저장소 추가, 제거된 저장소 복구/다른 위치로 복구/영구 삭제
- 작업자 수, Git timeout, 전체 브랜치 동기화 여부, stale lock 정리 정책 등 고급 옵션 저장
- 동기화 완료 후 라이브러리 루트의 `git_update_report.md` 열기

GUI 설정은 선택한 라이브러리 루트 아래 `.mygitpuller/config.json`에 저장됩니다. `.mygitpuller/removed`는 제거된 저장소를 임시 보관하는 영역이며, 복구 또는 영구 삭제 전까지 GUI의 제거 목록에서 관리됩니다.

#### 라이브러리 루트와 카테고리

GUI는 하나의 라이브러리 루트 아래에 카테고리 폴더를 두고 저장소를 관리합니다.

```
E:\FF14\Repos\Remotes\
├── Dalamud plugins\
│   └── SomePlugin\
├── Tools\
│   └── SomeTool\
└── .mygitpuller\
    ├── config.json
    └── removed\
```

URL 추가 시 사용자가 카테고리를 직접 선택합니다. 예를 들어 `https://github.com/goatcorp/Dalamud.git`을 `Tools` 카테고리에 추가하면 기본 대상은 `<라이브러리 루트>\Tools\Dalamud`입니다. 폴더 이름은 GUI에서 수정할 수 있으며, Core 검증을 통해 경로 이탈, `.mygitpuller` 내부 저장, Windows 예약 이름, trailing dot/space 같은 위험한 이름은 Git 실행 전에 거부됩니다.

## 사용 방법

`GitPuller.exe` (또는 `pull.bat`)를 관리하려는 프로젝트들의 상위 폴더에 위치시키거나, `--root` 옵션으로 경로를 지정하여 실행합니다.

기본 스캔 루트는 `GitPuller.exe`가 있는 폴더입니다. (원하면 `--root`로 덮어쓸 수 있습니다.)

```
/MyProjects/
├── /ProjectA/ (.git)
├── /ProjectB/ (.git)
├── /GitPuller/
│   ├── GitPuller.exe
│   ├── pull.bat
│   └── ...
```

### 옵션

- `-w <숫자>`: 병렬 작업 스레드 수를 설정합니다. (기본값: 6)
  ```bash
  GitPuller.exe -w 8
  ```

- `--rescan`: 캐시를 무시하고 모든 디렉터리를 다시 스캔하여 리포지토리를 찾습니다.
  ```bash
  GitPuller.exe --rescan
  ```

- `--init-missing-submodules`: (호환용) 초기화되지 않은 서브모듈이 있으면 자동으로 초기화(`init`)하고 업데이트합니다.
  - 현재는 기본 동작이 서브모듈 `--init --recursive` 업데이트이므로, 보통은 옵션이 필요 없습니다.

- `--no-init-submodules`: 서브모듈을 새로 초기화(`--init`)하지 않고, 이미 초기화된 서브모듈만 업데이트합니다.

- `--root <경로>`: 스캔할 루트 디렉터리를 지정합니다. (기본값: 실행 파일이 있는 디렉터리)
  ```bash
  GitPuller.exe --root "C:\Work\Projects"
  ```

- `-t <초>` / `--timeout <초>`: 각 `git` 명령의 타임아웃(초)을 설정합니다. (기본값: 60)
  ```bash
  GitPuller.exe -t 120
  ```

- `-h` / `--help`: 사용 가능한 옵션과 설명을 출력하고 종료합니다.
  ```bash
  GitPuller.exe --help
  ```

- `--no-pull`: `git pull --ff-only`를 생략하고 `fetch` 및 보고서 생성만 수행합니다.

- `--all-branches`: 모든 원격 브랜치를 로컬 tracking branch로 생성하거나 fast-forward합니다. 기본값입니다.

- `--current-branch-only`: 모든 로컬 브랜치 미러링은 생략하고, `origin/HEAD` 작업 트리만 강제 동기화합니다.

- `--force-sync`: 각 저장소의 로컬 브랜치 ref를 리모트 상태로 강제 동기화하고, 작업 트리는 기본 브랜치(`origin/HEAD`)로 강제 동기화합니다. 기본값입니다.

- `--clean`: `git clean -fdx`로 untracked/ignored 파일을 삭제합니다. 기본값입니다.

- `--stale-lock-minutes <분>`: 지정한 시간보다 오래된 Git `.lock` 파일만 stale lock으로 보고 삭제합니다. 기본값은 10분입니다.

- `--no-stale-lock-cleanup`: stale lock 자동 삭제를 비활성화합니다. lock 파일이 남아 있으면 해당 Git 명령은 실패할 수 있습니다.

- `--verbose-report`: 마크다운 리포트에 worker별 실행 상세와 개별 Git 명령 실행 시간을 포함합니다. 기본 리포트는 요약과 저장소별 결과만 기록합니다.

## 작동 방식

1. **실행 잠금:** 같은 루트 디렉터리를 대상으로 하는 다른 MyGitPuller 인스턴스가 있으면, `--timeout` 시간만큼 기다린 뒤 실패합니다.
2. **초기 실행:** 지정된 루트 디렉터리 하위의 모든 폴더를 재귀적으로 스캔하여 `.git` 폴더가 있는 리포지토리를 찾습니다.
3. **캐싱:** 찾은 리포지토리 목록을 `.git_repo_cache.json`에 저장합니다.
4. **업데이트:** 각 리포지토리에 대해 `git fetch --all --prune --prune-tags --tags --force`, 모든 원격 브랜치의 로컬 branch ref 강제 동기화, 기본 브랜치 작업 트리의 `reset --hard`/`clean`, `git submodule update` 등을 수행합니다.
   - 이 도구는 백업용이므로 기본 동작이 파괴적입니다. 로컬 변경, diverged 로컬 브랜치, untracked/ignored 파일은 보존하지 않습니다.
   - 원격 브랜치는 가능한 한 한 번의 `git update-ref --stdin` 배치로 로컬 branch ref에 반영합니다.
   - 원격에 없는 local-only 브랜치는 삭제합니다.
   - 원격에서 삭제된 태그도 로컬에서 pruning합니다.
   - Git LFS가 설치되어 있고 저장소에서 LFS를 사용하는 것으로 보이면 `git lfs fetch --all --prune`을 실행합니다.
   - 오래된 Git `.lock` 파일은 10분 이상 지난 경우 stale lock으로 보고 정리한 뒤 재시도합니다. 최근 lock은 실행 중인 Git 작업일 수 있으므로 삭제하지 않습니다.
   - 서브모듈은 기본적으로 `sync` + `update --init --recursive`로 최신 상태(슈퍼프로젝트가 가리키는 커밋)로 맞춥니다.
5. **결과:** 성공, 실패, 업데이트 변경 사항(커밋 로그 포함)을 콘솔에 출력하고 마크다운 리포트를 생성합니다.
   - 각 실행은 `git_update_report-<timestamp>.md`를 새로 생성합니다.
   - 호환성을 위해 최신 리포트는 `git_update_report.md`에도 함께 기록합니다.
   - 하나 이상의 저장소가 실패하면 프로그램 종료 코드는 `1`입니다.

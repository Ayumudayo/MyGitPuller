# MyGitPuller

여러 Git 리포지토리를 병렬로 빠르게 업데이트하는 C# 콘솔 애플리케이션입니다. 상위 디렉터리를 스캔하여 모든 리포지토리를 찾고, 최신 변경 사항을 가져오며(Pull), 서브모듈을 업데이트합니다.

## 시작하기

### 필수 조건

- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 이상 (실행 시)
- 또는 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (직접 빌드 시)
- Git이 설치되어 있고 시스템 PATH에 등록되어 있어야 합니다.

### 설치 및 실행

1. **빌드된 파일 사용**: `Publish` 폴더의 `GitPuller.exe`와 `pull.bat`를 원하는 위치로 복사합니다.
2. **실행**: `pull.bat`를 더블 클릭하거나 터미널에서 실행합니다.

```bash
pull.bat
```

## 사용 방법

`GitPuller.exe` (또는 `pull.bat`)를 관리하려는 프로젝트들의 상위 폴더에 위치시키거나, `--root` 옵션으로 경로를 지정하여 실행합니다.

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

- `--init-missing-submodules`: 초기화되지 않은 서브모듈이 있으면 자동으로 초기화(`init`)하고 업데이트합니다.
  ```bash
  GitPuller.exe --init-missing-submodules
  ```

- `--root <경로>`: 스캔할 루트 디렉터리를 지정합니다. (기본값: 현재 디렉터리 `.`)
  ```bash
  GitPuller.exe --root "C:\Work\Projects"
  ```

## 작동 방식

1. **초기 실행:** 지정된 루트 디렉터리 하위의 모든 폴더를 재귀적으로 스캔하여 `.git` 폴더가 있는 리포지토리를 찾습니다.
2. **캐싱:** 찾은 리포지토리 목록을 `.git_repo_cache.json`에 저장합니다.
3. **업데이트:** 각 리포지토리에 대해 `git fetch`, `git pull` (Fast-forward only), `git submodule update` 등을 수행합니다.
4. **결과:** 성공, 실패, 업데이트 변경 사항(커밋 로그 포함)을 콘솔에 출력하고 마크다운 리포트를 생성합니다.

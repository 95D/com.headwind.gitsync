# PixelSync — Git LFS Visual Tool for Unity

> **디자이너를 위한 유니티 에디터 내 Git / Git LFS 시각화 도구**  
> Git CLI 없이도 파일 상태 확인, 커밋, 원버튼 동기화, LFS 잠금/해제를 수행할 수 있습니다.

---

## 목차

1. [기능 요약](#기능-요약)
2. [설치 방법](#설치-방법)
3. [사용 방법](#사용-방법)
4. [아키텍처](#아키텍처)
5. [파일 구조](#파일-구조)
6. [자주 묻는 질문](#자주-묻는-질문)
7. [라이선스](#라이선스)

---

## 기능 요약

| 기능 | 설명 |
|------|------|
| **브랜치 표시** | 현재 체크아웃된 브랜치 이름을 에디터 상단에 상시 표시 |
| **변경 파일 목록** | `git status` 결과를 A / M / D / R / ? 뱃지와 함께 리스트 표시 |
| **LFS 잠금 표시** | 각 파일의 LFS 잠금 소유자를 목록에 표시 (내 잠금 🟢 / 타인 잠금 🔴) |
| **Lock / Unlock** | 파일 행 우측 버튼 한 번으로 `git lfs lock` / `git lfs unlock` 실행 |
| **원버튼 Sync** | `git add .` → `git commit` → `git pull --rebase` → `git push` 를 비동기 순서 실행 |
| **프로젝트 창 오버레이** | Project 창 아이콘 위에 변경 상태·잠금 상태 뱃지를 자동 렌더링 |

---

## 설치 방법

### 요구 사항

- Unity **2021.3 LTS** 이상
- Git ≥ 2.39 및 **Git LFS** 설치 및 초기화 완료  
  (`git lfs install` 을 한 번은 실행해 두어야 합니다)
- OS 수준 Git Credential Manager 설정 완료  
  (에디터 내 별도 로그인 UI 없음)
- **외부 의존성 없음** — 표준 .NET `System.Threading.Tasks` 만 사용합니다.

### UPM으로 설치 (로컬 패키지)

이 패키지는 유니티 프로젝트의 `Packages/` 폴더 내에 **로컬 패키지** 형태로 포함됩니다.  
Unity가 프로젝트를 열면 자동으로 인식하며, 별도의 추가 설치 과정이 없습니다.

---

## 사용 방법

### 1. 창 열기

```
Unity 메뉴 → Window → PixelSync → PixelSync Window
```

### 2. Refresh

상단의 **Refresh** 버튼을 클릭하면 현재 git 상태와 LFS 잠금 정보를 불러옵니다.  
창은 30초 간격으로 자동 갱신됩니다.

### 3. 파일 목록 확인

| 뱃지 | 의미 |
|------|------|
| `A` (초록) | 새로 추가된 파일 |
| `M` (노랑) | 수정된 파일 |
| `D` (빨강) | 삭제된 파일 |
| `R` (하늘) | 이름 변경된 파일 |
| `?` (회색) | Untracked 파일 |

### 4. LFS Lock / Unlock

- 파일 행 우측의 **Lock** 버튼 → `git lfs lock <파일>` 실행
- 내가 잠근 파일이라면 **Unlock** 버튼으로 해제
- 타인이 잠근 파일은 버튼이 비활성화됩니다 (`Locked` 표시)

### 5. 원버튼 Sync

1. 하단의 커밋 메시지 입력창에 메시지를 작성합니다.
2. **Sync (add → commit → pull → push)** 버튼 클릭
3. 내부적으로 아래 순서를 비동기 실행합니다:
   ```
   git add .
   git commit -m "<메시지>"
   git pull --rebase
   git push
   ```
4. 완료 후 파일 목록이 자동 갱신됩니다.

### 6. 프로젝트 창 오버레이

PixelSync 창을 열지 않아도 **Project 탭** 의 파일 아이콘 위에 뱃지가 표시됩니다.

- 우측 상단: 변경 상태 뱃지 (`✎` / `+` / `−` 등)
- 우측 하단: LFS 잠금 뱃지 (`🔒`)
  - 초록색: 내가 잠근 파일
  - 빨간색: 타인이 잠근 파일

---

## 아키텍처

**Clean Architecture + MVVM** 3-레이어 구조를 따릅니다.

```
Presentation Layer  (ViewModel, View)
      │
      ▼
Domain Layer        (Use Cases, IGitRepository interface)
      │
      ▼
Data Layer          (GitRepository, GitProcessUtility, GitDataParser, Models)
```

### 레이어별 역할

#### Data Layer (`Editor/Data/`)

| 클래스 | 역할 |
|--------|------|
| `GitProcessUtility` | `System.Diagnostics.Process`를 `Task.Run`으로 래핑해 git CLI를 비동기 실행 |
| `GitDataParser` | `git status --porcelain`, `git lfs locks` 텍스트를 C# 모델로 파싱 |
| `GitRepository` | `IGitRepository` 구현체. 위 두 클래스를 조합해 실제 I/O 처리 |
| `Models/FileState` | 단일 파일의 변경 상태 + LFS 잠금 정보를 담는 데이터 클래스 |
| `Models/LfsLockInfo` | LFS 잠금 ID, 소유자명, 내 잠금 여부를 담는 데이터 클래스 |

#### Domain Layer (`Editor/Domain/`)

| 클래스 | 역할 |
|--------|------|
| `IGitRepository` | Data/Presentation 간 의존성 역전을 위한 인터페이스 |
| `GetGitStatusUseCase` | 브랜치 이름 + 파일 상태 목록을 병렬 조회 |
| `SyncUseCase` | 원버튼 Sync 워크플로 실행 |
| `LockFileUseCase` | LFS lock 실행 |
| `UnlockFileUseCase` | LFS unlock 실행 |

#### Presentation Layer (`Editor/Presentation/`)

| 클래스 | 역할 |
|--------|------|
| `PixelSyncViewModel` | UI 상태 보유, 커맨드 노출, 상태 변경 시 `OnStateChanged` 이벤트 발행 |
| `PixelSyncWindow` | IMGUI 기반 `EditorWindow`. ViewModel 을 구독해 Repaint |
| `ProjectWindowOverlay` | `[InitializeOnLoad]` 로 자동 등록, Project 창 아이콘에 오버레이 렌더링 |

### 비동기 처리

- 모든 git CLI 호출은 `Task.Run` 위에서 실행되어 **Unity 메인 스레드를 블로킹하지 않습니다**.
- 에디터 전용 코드이므로 외부 라이브러리 없이 표준 `System.Threading.Tasks.Task` + `async/await` 를 사용합니다.
- UI 상태 변경은 `EditorApplication.delayCall`을 통해 메인 스레드로 안전하게 전달합니다.

---

## 파일 구조

```
Packages/com.yourteam.pixelsync/
├── package.json                              # UPM 패키지 메타데이터 (외부 의존성 없음)
├── Com.YourTeam.PixelSync.Editor.asmdef     # Editor 전용 어셈블리 정의
├── README.md
└── Editor/
    ├── Data/
    │   ├── Models/
    │   │   ├── FileState.cs                 # 파일 상태 모델
    │   │   └── LfsLockInfo.cs               # LFS 잠금 모델
    │   ├── GitProcessUtility.cs             # 비동기 git 프로세스 실행기
    │   ├── GitDataParser.cs                 # git 출력 텍스트 파서
    │   └── GitRepository.cs                 # IGitRepository 구현체
    ├── Domain/
    │   ├── Interfaces/
    │   │   └── IGitRepository.cs            # 레이어 경계 인터페이스
    │   └── UseCases/
    │       ├── GetGitStatusUseCase.cs
    │       ├── SyncUseCase.cs
    │       ├── LockFileUseCase.cs
    │       └── UnlockFileUseCase.cs
    └── Presentation/
        ├── ViewModels/
        │   └── PixelSyncViewModel.cs        # UI 상태 & 커맨드
        └── Views/
            ├── PixelSyncWindow.cs           # 메인 EditorWindow (IMGUI)
            └── ProjectWindowOverlay.cs      # Project 창 아이콘 오버레이
```

---

## 자주 묻는 질문

**Q. Git LFS가 설치되지 않은 환경에서도 동작하나요?**  
A. 네. LFS lock 관련 명령어는 실패해도 오버레이를 무시하고 기본 git status 기능은 정상 동작합니다.

**Q. 사용자 인증(로그인)은 어디서 하나요?**  
A. 이 도구는 OS 수준의 Git Credential Manager를 전제로 합니다. 별도 로그인 UI가 없으므로, 사전에 터미널에서 `git push` 를 한 번 성공시켜 자격증명을 저장해 두어야 합니다.

**Q. 브랜치 전환, 머지 등 고급 기능은?**  
A. 이 도구는 디자이너 대상으로 **필수 기능만** 제공합니다. 브랜치 관리 등 복잡한 작업은 Git GUI 클라이언트(SourceTree, Fork 등)와 병행 사용을 권장합니다.

**Q. Sync 도중 충돌(Conflict)이 발생하면?**  
A. `git pull --rebase` 에서 충돌이 감지되면 Sync가 중단되고 하단 상태 바에 오류 메시지가 표시됩니다. 충돌 해결은 Git GUI 클라이언트 또는 터미널에서 수행 후 재시도하세요.

---

## 라이선스

MIT License — 자유롭게 사용, 수정, 재배포 가능합니다.

---

*PixelSync — Made for pixel artists who just want to ship.*

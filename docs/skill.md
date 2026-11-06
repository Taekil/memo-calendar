# Claude 스킬

`skill/memo-calendar/SKILL.md`는 Claude가 `memos.json`을 읽고, 요약하고, 추가·수정·삭제하게 하는 스킬입니다.

## 무엇을 하나

- **읽기와 요약:** "지난주 메모 요약해줘", "이번 달 메모 정리해줘". 주는 일요일~토요일로 계산하고, 주제별로 묶어 짧게 정리합니다. 메모에 없는 내용은 지어내지 않습니다.
- **쓰기:** "내일 치과 예약 추가해줘". 변경 목록(날짜와 문장)을 먼저 보여 주고 확인을 받은 뒤, 백업을 만들고 저장하고, 다시 읽어 항목 수를 확인합니다.
- **지키는 것:** 메모 문장을 구조화하거나 다듬지 않고 말한 표현 그대로 넣습니다. 삭제는 사용자가 명시한 메모만 합니다.

"메모 달력"이라는 말을 문장에 넣으세요. "일정"이라고만 하면 Claude가 진짜 캘린더 연결(Outlook 등)을 먼저 찾아갈 수 있습니다.

## 설치

### PC (Claude Code)

`skill/memo-calendar` 폴더를 `~/.claude/skills/memo-calendar`로 복사합니다. Claude Code가 로컬 파일 경로로 `memos.json`을 직접 읽고 씁니다.

### claude.ai (웹, 휴대폰)

1. `skill/memo-calendar` 폴더를 `memo-calendar.zip`으로 압축합니다. zip 안에 `memo-calendar/SKILL.md`가 있어야 합니다.
2. PC 브라우저에서 claude.ai → 설정 → 기능(Capabilities) → 스킬에서 zip을 올립니다.
3. 휴대폰 앱에서는 같은 계정의 스킬이 그대로 보입니다. 휴대폰에서는 직접 올릴 수 없어서 PC 브라우저가 필요합니다.
4. 휴대폰과 웹은 파일 시스템이 없으므로 **Microsoft 365 연결(SharePoint/OneDrive)** 이 켜져 있어야 합니다.

## 꼭 고칠 곳

`SKILL.md`의 SharePoint 주소는 자리표시자입니다. 자신의 OneDrive 주소로 바꾸세요.

```
https://<조직>-my.sharepoint.com/personal/<계정>/Documents/memo-calendar/memos.json
```

## 알려진 한계

- 휴대폰에서 읽기와 요약은 잘 되지만, 쓰기는 Microsoft 365 연결에 기대는 부분이라 불안정할 수 있습니다. 급한 메모는 PC 앱에서 적는 것이 확실합니다.
- OneDrive 동기화가 늦으면 앱에 반영되는 데 시간이 걸립니다. 트레이 툴팁의 마지막 갱신 시각으로 확인하세요.
- 쓰기 전에 항상 `backup\`에 사본이 생깁니다. 잘못 고쳤다면 최근 백업을 `memos.json`으로 되돌리면 됩니다.
